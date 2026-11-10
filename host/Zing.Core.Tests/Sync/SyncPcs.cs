using Zing.Core.Sync;
using Zing.Core.Tests.Vault;
using Zing.Core.Vault;

namespace Zing.Core.Tests.Sync;

/// <summary>A clock a test moves: each read ticks one millisecond on from where the test put it.</summary>
internal sealed class SettableClock(long start = 1_800_000_000_000) : IClock
{
	private long _time = start;

	public long Now() => Interlocked.Increment(ref _time);

	public void Advance(TimeSpan by) => Interlocked.Add(ref _time, (long)by.TotalMilliseconds);

	public IDisposable OnAdvance(Action listener) => new Nothing();

	private sealed class Nothing : IDisposable
	{
		public void Dispose() { }
	}
}

/// <summary>The server's clock, which a test moves forward (tombstones are purged 30 days after their last write).</summary>
internal sealed class ServerClock : TimeProvider
{
	private DateTimeOffset _now = DateTimeOffset.UtcNow;

	public override DateTimeOffset GetUtcNow() => _now;

	public void Advance(TimeSpan by) => _now += by;
}

/// <summary>Runs scheduled work only when a test fires it, from any thread: the engine schedules from its own continuations.</summary>
internal sealed class TestScheduler : IScheduler
{
	private sealed class Entry(TimeSpan delay, Action action) : IDisposable
	{
		public TimeSpan Delay { get; } = delay;
		public Action Action { get; } = action;
		public bool Cancelled { get; private set; }

		public void Dispose() => Cancelled = true;
	}

	private readonly Lock _gate = new();
	private readonly List<Entry> _entries = [];

	public IDisposable Schedule(TimeSpan delay, Action action)
	{
		var entry = new Entry(delay, action);
		lock (_gate) _entries.Add(entry);
		return entry;
	}

	/// <summary>The delays of the work still waiting, oldest first.</summary>
	public IReadOnlyList<TimeSpan> Pending
	{
		get
		{
			lock (_gate) return [.. _entries.Where(e => !e.Cancelled).Select(e => e.Delay)];
		}
	}

	/// <summary>Runs every waiting entry scheduled with <paramref name="delay"/>; at least one must be waiting.</summary>
	public void Fire(TimeSpan delay)
	{
		List<Entry> due;
		lock (_gate)
		{
			due = [.. _entries.Where(e => !e.Cancelled && e.Delay == delay)];
			_entries.RemoveAll(e => e.Cancelled || due.Contains(e));
		}
		if (due.Count == 0) throw new InvalidOperationException($"Nothing is scheduled after {delay}");
		foreach (var entry in due) entry.Action();
	}
}

/// <summary>One PC of a test: its own vault folder, store, client and engine, against the shared local server.</summary>
internal sealed class SyncPc : IDisposable
{
	public string Dir { get; }
	public FakeSealer Sealer { get; } = new();
	public SettableClock Clock { get; } = new();
	public TestScheduler Scheduler { get; } = new();
	public VaultStore Vault { get; private set; }
	public SyncClient Client { get; private set; }
	public SyncEngine Engine { get; private set; }

	private readonly LocalSyncServer _server;

	public SyncPc(LocalSyncServer server)
	{
		_server = server;
		Dir = Directory.CreateTempSubdirectory("zing-syncpc-").FullName;
		(Vault, Client, Engine) = Open();
	}

	private (VaultStore, SyncClient, SyncEngine) Open()
	{
		var vault = new VaultStore(Dir, new FakeCipher(), Sealer, Clock, new ManualScheduler(), new FakeSessionEvents());
		var client = new SyncClient(_server.Address, vault, _server.Handler());
		return (vault, client, new SyncEngine(vault, client, Scheduler, Clock));
	}

	/// <summary>Zing closing and starting again on this PC: everything in memory goes, the files stay.</summary>
	public void Restart()
	{
		Engine.Dispose();
		Client.Dispose();
		Vault.Dispose();
		(Vault, Client, Engine) = Open();
	}

	public async Task SignInAsync(string sub = LocalSyncServer.AllowedSub) => await Client.SignInAsync(await _server.MintIdTokenAsync(sub), TestContext.Current.CancellationToken);

	public Task SyncAsync() => Engine.SyncAsync(TestContext.Current.CancellationToken);

	public LoginSummary Login(string id) => Vault.Summaries().Single(s => s.Id == id);

	public void Dispose()
	{
		Engine.Dispose();
		Client.Dispose();
		Vault.Dispose();
		Directory.Delete(Dir, recursive: true);
	}
}

/// <summary>Two PCs syncing one vault: A turned sync on, and B restored from sync with the Zing password (Task 14).</summary>
internal sealed class TwoPcs : IDisposable
{
	/// <summary>A passphrase the generator made, five words and a digit (passwordStrength.vectors.json).</summary>
	public const string Strong = "teaching-focused-onward-activist-steel4";

	private readonly string _accountDir = Directory.CreateTempSubdirectory("zing-syncpcs-account-").FullName;
	private readonly string _bAccountDir = Directory.CreateTempSubdirectory("zing-syncpcs-account-b-").FullName;

	public LocalSyncServer Server { get; }
	public SyncPc A { get; }
	public AccountStore Account { get; }
	private SyncPc? _b;

	public SyncPc B => _b ?? throw new InvalidOperationException("Call JoinB first");

	public TwoPcs(ServerClock? clock = null)
	{
		Server = new LocalSyncServer(clock);
		A = new SyncPc(Server);
		Account = new AccountStore(_accountDir, new FakeCipher());
		Account.SignUp("ada", "Ada Lovelace", "ada.profile@example.test", Strong);
	}

	private static CancellationToken Ct => TestContext.Current.CancellationToken;

	/// <summary>A signs in and turns sync on, uploading what it holds.</summary>
	public async Task TurnOnAAsync()
	{
		await A.SignInAsync();
		await new SyncKeys(Account, A.Vault, A.Client).TurnOnAsync(Strong, Ct);
	}

	/// <summary>B signs in as the same Google account, restores from sync with the Zing password, and syncs once.</summary>
	public async Task JoinBAsync()
	{
		_b = new SyncPc(Server);
		await B.SignInAsync();
		await new SyncRestore(new AccountStore(_bAccountDir, new FakeCipher()), () => B.Vault, B.Client, B.Vault).RestoreWithPasswordAsync(Strong, Ct);
		await B.SyncAsync();
	}

	public async Task<string> SaveOnAAsync(string origin = "https://bank.example.test", string username = "ada", string password = "first-password") =>
		await A.Vault.SaveAsync(new NewLogin(origin, username, password, "Bank", ""));

	/// <summary>Everything the server holds now, read from revision 0 with A's sign-in, which stores nothing in A's vault.</summary>
	public async Task<SyncChanges> ServerItemsAsync()
	{
		var user = A.Vault.SyncAccount()!.UserId;
		var items = new List<SyncItem>();
		long since = 0;
		while (true)
		{
			var page = await A.Client.GetChangesAsync(since, cancellation: Ct, asUser: user);
			items.AddRange(page.Items);
			if (!page.More) return page with { Items = items };
			since = page.Items[^1].Revision;
		}
	}

	public void Dispose()
	{
		_b?.Dispose();
		A.Dispose();
		Server.Dispose();
		Directory.Delete(_accountDir, recursive: true);
		Directory.Delete(_bAccountDir, recursive: true);
	}
}
