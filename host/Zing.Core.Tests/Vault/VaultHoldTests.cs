using Zing.Core.Vault;

namespace Zing.Core.Tests.Vault;

/// <summary>Stands in for Windows locking or the session switching (WM_WTSSESSION_CHANGE in the host).</summary>
internal sealed class FakeSessionEvents : ISessionEvents
{
	public bool Watching { get; set; } = true;

	public event Action? Switched;

	public void Lock() => Switched?.Invoke();

	/// <summary>What the host does when Windows won't register a window for session notifications (Ruling 47).</summary>
	public void Unwatch()
	{
		Watching = false;
		Switched?.Invoke();
	}

	public int Listeners => Switched?.GetInvocationList().Length ?? 0;
}

/// <summary>
/// Ruling 44 (the owner's decision of 2026-11-07): a successful Hello unseal keeps the secret key for a fixed 5 minutes, not
/// sliding; export asks every time; turning the vault off, Windows locking, Zing closing and the store going all wipe it.
/// </summary>
public sealed class VaultHoldTests : IDisposable
{
	private static readonly NewLogin Login = new("https://example.com", "ada", "first password", "", "");
	private static readonly TimeSpan Second = TimeSpan.FromSeconds(1);

	private readonly string _dir = Directory.CreateTempSubdirectory("zing-vault-hold-").FullName;
	private readonly FakeSealer _sealer = new();
	private readonly ManualScheduler _scheduler = new();
	private readonly FakeSessionEvents _session = new();

	public void Dispose() => Directory.Delete(_dir, recursive: true);

	private VaultStore Open() => new(_dir, new FakeCipher(), _sealer, new TickingClock(), _scheduler, _session);

	/// <summary>A store with one login whose keys exist, and nothing held yet (the first save seals, it doesn't unseal).</summary>
	private async Task<(VaultStore Store, string Id)> Saved()
	{
		var store = Open();
		var id = await store.SaveAsync(Login);
		Assert.Equal(0, _sealer.Unseals);
		return (store, id);
	}

	[Fact]
	public async Task ASecondFillInsideFiveMinutesAsksNothingAndOneAtFiveMinutesAndASecondAsksAgain()
	{
		var (store, id) = await Saved();

		Assert.Equal("first password", await store.PasswordAsync(id));
		Assert.Equal(1, _sealer.Unseals);
		_scheduler.Advance(VaultStore.SecretKeyHeldFor - Second);
		Assert.Equal("first password", await store.PasswordAsync(id));
		Assert.Equal(1, _sealer.Unseals);

		_scheduler.Advance(Second + Second);
		Assert.Equal("first password", await store.PasswordAsync(id));
		Assert.Equal(2, _sealer.Unseals);
	}

	[Fact]
	public async Task TheFiveMinutesRunFromTheHelloAndUseDoesNotExtendThem()
	{
		var (store, id) = await Saved();
		await store.PasswordAsync(id);

		_scheduler.Advance(TimeSpan.FromMinutes(4));
		await store.PasswordAsync(id);
		await store.UpdatePasswordAsync(id, "second password");
		Assert.Equal(1, _sealer.Unseals);
		_scheduler.Advance(TimeSpan.FromMinutes(1) + Second);

		Assert.Equal("second password", await store.PasswordAsync(id));
		Assert.Equal(2, _sealer.Unseals);
	}

	[Fact]
	public async Task SaveUpdateHistoryAndImportInsideTheFiveMinutesAskNothing()
	{
		var (store, id) = await Saved();
		await store.PasswordAsync(id);

		await store.SaveAsync(new NewLogin("https://other.example", "bob", "pw", "", ""));
		await store.UpdateLoginAsync(id, "t", "n", "ada", "changed");
		Assert.True(await store.UpdateLoginIfChangedAsync(id, "t", "n", "ada", "changed again"));
		await store.HistoryPasswordAsync(id, 0);
		await store.ImportAsync([new NewLogin("https://third.example", "carol", "pw", "", "")]);

		Assert.Equal(1, _sealer.Unseals);
	}

	[Fact]
	public async Task ExportAsksHelloEveryTimeEvenInsideTheFiveMinutes()
	{
		var (store, id) = await Saved();
		await store.PasswordAsync(id);

		await store.ExportAsync();
		await store.ExportAsync();
		Assert.Equal(3, _sealer.Unseals);

		_sealer.Result = HelloResult.Cancel;
		await Assert.ThrowsAsync<HelloCancelledException>(() => store.ExportAsync());
		// The fill's key is still held: export neither used nor wiped it.
		Assert.Equal("first password", await store.PasswordAsync(id));
	}

	[Fact]
	public async Task ExportStartsNoHold()
	{
		var (store, id) = await Saved();

		await store.ExportAsync();
		await store.PasswordAsync(id);

		Assert.Equal(2, _sealer.Unseals);
	}

	[Fact]
	public async Task ACancelledHelloHoldsNothing()
	{
		var (store, id) = await Saved();
		_sealer.Result = HelloResult.Cancel;
		await Assert.ThrowsAsync<HelloCancelledException>(() => store.PasswordAsync(id));

		_sealer.Result = HelloResult.Pass;
		await store.PasswordAsync(id);
		await store.PasswordAsync(id);

		Assert.Equal(2, _sealer.Unseals);
	}

	[Fact]
	public async Task NothingIsScheduledUntilAHelloSucceedsAndTheTimerGoesWithTheKey()
	{
		var (store, id) = await Saved();
		Assert.Equal(0, _scheduler.PendingCount);

		await store.PasswordAsync(id);
		Assert.Equal(1, _scheduler.PendingCount);

		store.ForgetSecretKey();
		Assert.Equal(0, _scheduler.PendingCount);
	}

	[Fact]
	public async Task DisposingTheStoreWipesTheKey()
	{
		var (store, id) = await Saved();
		await store.PasswordAsync(id);

		store.Dispose();

		await store.PasswordAsync(id);
		Assert.Equal(2, _sealer.Unseals);
		Assert.Equal(0, _scheduler.PendingCount);
	}

	[Fact]
	public async Task AStoreDisposedWhileHelloIsUpHoldsNothing()
	{
		var (store, id) = await Saved();
		_sealer.Unsealing = () =>
		{
			_sealer.Unsealing = null;
			store.Dispose();
		};

		await store.PasswordAsync(id);
		await store.PasswordAsync(id);

		Assert.Equal(2, _sealer.Unseals);
	}

	[Fact]
	public async Task HelloStoppingBeingAvailableWipesTheKey()
	{
		var (store, id) = await Saved();
		await store.PasswordAsync(id);

		_sealer.Result = HelloResult.Unavailable;
		_sealer.Announce();
		_sealer.Result = HelloResult.Pass;
		_sealer.Announce();

		await store.PasswordAsync(id);
		Assert.Equal(2, _sealer.Unseals);
	}

	private (SettingsStore Settings, VaultSwitch Switch) Switch()
	{
		var settings = new SettingsStore();
		settings.Set("passwordAutofill", true);
		return (settings, new VaultSwitch(settings, Open, e => throw new Xunit.Sdk.XunitException($"the vault couldn't be read: {e}")));
	}

	[Fact]
	public async Task TurningTheVaultOffWipesTheKeySoTheNextUseAsksAgain()
	{
		var (settings, vaults) = Switch();
		using var owned = vaults;
		var first = vaults.Current!;
		var id = await first.SaveAsync(Login);
		await first.PasswordAsync(id);

		settings.Set("passwordAutofill", false);

		// The store that held it holds nothing now, and the store that replaces it starts with nothing.
		await first.PasswordAsync(id);
		Assert.Equal(2, _sealer.Unseals);
		settings.Set("passwordAutofill", true);
		await vaults.Current!.PasswordAsync(id);
		Assert.Equal(3, _sealer.Unseals);
	}

	[Fact]
	public async Task ReplacingTheStoreWipesTheOldOnesKey()
	{
		var (settings, vaults) = Switch();
		using var owned = vaults;
		var first = vaults.Current!;
		var id = await first.SaveAsync(Login);
		await first.PasswordAsync(id);

		settings.Set("passwordAutofill", false);
		settings.Set("passwordAutofill", true);

		Assert.NotSame(first, vaults.Current);
		await vaults.Current!.PasswordAsync(id);
		await first.PasswordAsync(id);
		Assert.Equal(3, _sealer.Unseals);
	}

	[Fact]
	public async Task WindowsLockingWipesTheKeySoTheNextUseAsksAgain()
	{
		var (store, id) = await Saved();
		await store.PasswordAsync(id);

		_session.Lock();

		await store.PasswordAsync(id);
		Assert.Equal(2, _sealer.Unseals);
		await store.PasswordAsync(id);
		Assert.Equal(2, _sealer.Unseals);
	}

	[Fact]
	public async Task AHelloAnsweredAfterWindowsLockedHoldsNothing()
	{
		var (store, id) = await Saved();
		_sealer.Unsealing = () =>
		{
			_sealer.Unsealing = null;
			_session.Lock();
		};

		await store.PasswordAsync(id);
		await store.PasswordAsync(id);

		Assert.Equal(2, _sealer.Unseals);
	}

	[Fact]
	public async Task ClosingZingDisposesTheSwitchWhichWipesTheKeyAndStopsListeningForLocks()
	{
		var (_, vaults) = Switch();
		var store = vaults.Current!;
		var id = await store.SaveAsync(Login);
		await store.PasswordAsync(id);
		Assert.Equal(1, _session.Listeners);

		vaults.Dispose();

		Assert.Equal(0, _session.Listeners);
		await store.PasswordAsync(id);
		Assert.Equal(2, _sealer.Unseals);
	}

	/// <summary>Ruling 47: Windows wouldn't report locks to a window, so a held key could outlive a lock; Hello asks every time.</summary>
	[Fact]
	public async Task WithoutLockDetectionNothingIsHeldAndHelloAsksEveryTime()
	{
		_session.Watching = false;
		var (store, id) = await Saved();

		await store.PasswordAsync(id);
		await store.PasswordAsync(id);
		await store.UpdatePasswordAsync(id, "second password");

		Assert.Equal(3, _sealer.Unseals);
		Assert.Equal(0, _scheduler.PendingCount);
	}

	[Fact]
	public async Task LosingLockDetectionWipesAKeyAlreadyHeld()
	{
		var (store, id) = await Saved();
		await store.PasswordAsync(id);

		_session.Unwatch();

		await store.PasswordAsync(id);
		await store.PasswordAsync(id);
		Assert.Equal(3, _sealer.Unseals);
	}
}
