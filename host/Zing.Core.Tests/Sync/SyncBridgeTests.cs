using System.Text.Json;
using Zing.Core.Net;
using Zing.Core.Sync;
using Zing.Core.Tests.Vault;
using Zing.Core.Vault;

namespace Zing.Core.Tests.Sync;

/// <summary>Settings' Turn on sync over the real bridge, against the local server; Google is the fake issuer's stand-in.</summary>
public sealed class SyncBridgeTests : IDisposable
{
	private readonly string _dir = Directory.CreateTempSubdirectory("zing-syncbridge-").FullName;
	private readonly LocalSyncServer _server = new();
	private readonly VaultStore _vault;
	private readonly LibraryStore _library = new(Library.EmptyLibrary(0), _ => { }, Deterministic.Clock(), Deterministic.Ids("id-"));
	private readonly List<ChromeApi> _apis = [];

	private readonly FakeSealer _sealer = new();

	public SyncBridgeTests() =>
		_vault = new VaultStore(_dir, new FakeCipher(), _sealer, new TickingClock(), new ManualScheduler(), new FakeSessionEvents());

	public void Dispose()
	{
		foreach (var api in _apis) api.Dispose();
		_vault.Dispose();
		_server.Dispose();
		Directory.Delete(_dir, recursive: true);
	}

	/// <summary>A Google sign-in the person never finishes: it waits until it is cancelled.</summary>
	private sealed class NeverFinishedSignIn : IGoogleSignIn
	{
		public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public async Task<string> SignInAsync(CancellationToken cancellation)
		{
			Started.SetResult();
			await Task.Delay(Timeout.Infinite, cancellation);
			throw new InvalidOperationException("unreachable");
		}
	}

	private sealed class FakeIssuerSignIn(LocalSyncServer server) : IGoogleSignIn
	{
		public int Calls { get; private set; }

		public Task<string> SignInAsync(CancellationToken cancellation)
		{
			Calls++;
			return server.MintIdTokenAsync();
		}
	}

	private readonly FakeWindowHost _host = new();

	private Bridge Api(SyncSetup? sync, VaultStore? vault = null, AccountStore? account = null)
	{
		var bridge = new Bridge();
		_apis.Add(new ChromeApi(bridge, new AppServices(new SettingsStore(), _library, Address.FromDefaults(), new TickingClock(), account ?? Accounts.Guest(), new RelayConsent(), Vault: vault ?? _vault, Sync: sync), _host, "test"));
		return bridge;
	}

	private List<SyncState> PushedStates() => [.. _host.Pushed.Where(p => p.Channel == "sync:state").Select(p => (SyncState)p.Payload!)];

	private static string? Notice(JsonElement state) => state.TryGetProperty("notice", out var notice) ? notice.GetString() : null;

	private SyncSetup Setup(IGoogleSignIn signIn) => new(_server.Address, signIn, _server.Handler());

	private static async Task<JsonElement> Invoke(Bridge bridge, string channel, params object[] args)
	{
		var json = JsonSerializer.Serialize(new { kind = "invoke", id = 1, channel, args });
		return JsonDocument.Parse(await bridge.HandleAsync(json)).RootElement;
	}

	private static JsonElement Value(JsonElement result)
	{
		if (!result.GetProperty("ok").GetBoolean()) throw new Xunit.Sdk.XunitException(result.GetProperty("error").ToString());
		return result.GetProperty("value");
	}

	[Fact]
	public async Task TurningOnSyncSignsInAndShowsTheEmail()
	{
		var bridge = Api(Setup(new FakeIssuerSignIn(_server)));
		Assert.False(Value(await Invoke(bridge, "sync:get")).GetProperty("signedIn").GetBoolean());

		var state = Value(await Invoke(bridge, "sync:sign-in"));

		Assert.True(state.GetProperty("signedIn").GetBoolean());
		Assert.Equal(LocalSyncServer.Email, state.GetProperty("email").GetString());
		Assert.Equal(LocalSyncServer.Email, _vault.SyncAccount()!.Email);
		Assert.Equal(LocalSyncServer.Email, Value(await Invoke(bridge, "sync:get")).GetProperty("email").GetString());
	}

	[Fact]
	public async Task SigningOutRevokesOnTheServerAndForgetsTheAccount()
	{
		var bridge = Api(Setup(new FakeIssuerSignIn(_server)));
		Value(await Invoke(bridge, "sync:sign-in"));

		var state = Value(await Invoke(bridge, "sync:sign-out"));

		Assert.False(state.GetProperty("signedIn").GetBoolean());
		Assert.Null(Notice(state));
		Assert.Null(_vault.SyncAccount());
		Assert.Equal(1, _server.Wire.Count("POST /auth/signout"));
	}

	[Fact]
	public async Task SigningOutWithTheServerDownForgetsTheAccountAndSaysTheServerCopyExpires()
	{
		var bridge = Api(Setup(new FakeIssuerSignIn(_server)));
		Value(await Invoke(bridge, "sync:sign-in"));
		_server.Wire.Down = true;

		var state = Value(await Invoke(bridge, "sync:sign-out"));

		Assert.False(state.GetProperty("signedIn").GetBoolean());
		Assert.Equal("kept-on-server", Notice(state));
		Assert.Null(_vault.SyncAccount());
	}

	[Fact]
	public async Task ASignInTheServerEndedSaysSoUntilTheNextSignIn()
	{
		var bridge = Api(Setup(new FakeIssuerSignIn(_server)));
		Value(await Invoke(bridge, "sync:sign-in"));
		_vault.MarkSyncSignedOut();

		Assert.Equal("signed-out", Notice(Value(await Invoke(bridge, "sync:get"))));
		Value(await Invoke(bridge, "sync:sign-in"));
		Assert.Null(Notice(Value(await Invoke(bridge, "sync:get"))));
	}

	[Fact]
	public async Task AnUnreadableSignInIsReportedAndSigningInAgainReplacesIt()
	{
		_vault.SaveSyncAccount(new SyncAccount("user", "old@example.test", "old"));
		using var foreign = new VaultStore(_dir, new FakeCipher(foreign: true), new FakeSealer(), new TickingClock(), new ManualScheduler(), new FakeSessionEvents());
		var bridge = Api(Setup(new FakeIssuerSignIn(_server)), foreign);

		var state = Value(await Invoke(bridge, "sync:get"));
		Assert.False(state.GetProperty("signedIn").GetBoolean());
		Assert.Equal("unreadable", Notice(state));

		var again = Value(await Invoke(bridge, "sync:sign-in"));
		Assert.True(again.GetProperty("signedIn").GetBoolean());
		using var readable = new VaultStore(_dir, new FakeCipher(), new FakeSealer(), new TickingClock(), new ManualScheduler(), new FakeSessionEvents());
		Assert.Equal(LocalSyncServer.Email, readable.SyncAccount()!.Email);
	}

	[Fact]
	public async Task CancellingASignInEndsItWithoutAnError()
	{
		var signIn = new NeverFinishedSignIn();
		var bridge = Api(Setup(signIn));

		var signingIn = Invoke(bridge, "sync:sign-in");
		await signIn.Started.Task;
		Assert.True((await Invoke(bridge, "sync:cancel-sign-in")).GetProperty("ok").GetBoolean());
		var state = Value(await signingIn);

		Assert.False(state.GetProperty("signedIn").GetBoolean());
		Assert.Null(_vault.SyncAccount());
	}

	[Fact]
	public async Task EveryChangeIsPushedToTheWindow()
	{
		var bridge = Api(Setup(new FakeIssuerSignIn(_server)));

		Value(await Invoke(bridge, "sync:sign-in"));
		Value(await Invoke(bridge, "sync:sign-out"));

		var pushed = PushedStates();
		Assert.Contains(pushed, s => s.SignedIn && s.Email == LocalSyncServer.Email);
		Assert.False(pushed[^1].SignedIn);
	}

	[Fact]
	public async Task ABuildWithoutAGoogleClientSaysSyncIsNotSetUp()
	{
		var bridge = Api(sync: null);

		var error = (await Invoke(bridge, "sync:sign-in")).GetProperty("error");

		Assert.Equal(nameof(SyncNotSetUpException), error.GetProperty("name").GetString());
		Assert.Equal("Sync isn't set up in this build of Zing", error.GetProperty("message").GetString());
	}

	[Fact]
	public async Task SigningInWhileSignedInAsksGoogleAgain()
	{
		var signIn = new FakeIssuerSignIn(_server);
		var bridge = Api(Setup(signIn));
		Value(await Invoke(bridge, "sync:sign-in"));
		var first = _vault.SyncAccount()!.Refresh;

		Value(await Invoke(bridge, "sync:sign-in"));

		Assert.Equal(2, signIn.Calls);
		Assert.NotEqual(first, _vault.SyncAccount()!.Refresh);
	}

	/// <summary>A five-word passphrase the generator made (passwordStrength.vectors.json).</summary>
	private const string Strong = "teaching-focused-onward-activist-steel";

	private static AccountStore AccountWith(string password)
	{
		var account = new AccountStore(Directory.CreateTempSubdirectory("zing-syncbridge-account-").FullName, new FakeCipher());
		account.SignUp("ada", "Ada", "ada@example.test", password);
		return account;
	}

	private static bool RecoveryPending(JsonElement state) => state.GetProperty("recoveryPending").GetBoolean();

	[Fact]
	public async Task TurningOnGivesTheRecoveryKeyOnceAndSyncWaitsUntilItIsConfirmedSaved()
	{
		var bridge = Api(Setup(new FakeIssuerSignIn(_server)), account: AccountWith(Strong));
		Value(await Invoke(bridge, "sync:sign-in"));
		Assert.False(Value(await Invoke(bridge, "sync:get")).GetProperty("on").GetBoolean());

		var turnedOn = Value(await Invoke(bridge, "sync:turn-on", Strong));

		var recovery = turnedOn.GetProperty("recoveryKey").GetString()!;
		Assert.Matches("^[A-Z2-7]{4}(-[A-Z2-7]{4}){5}-[A-Z2-7]{2}$", recovery);
		var state = Value(await Invoke(bridge, "sync:get"));
		Assert.True(state.GetProperty("on").GetBoolean());
		Assert.True(RecoveryPending(state));
		Assert.Contains(PushedStates(), s => s.On && s.RecoveryPending);

		var confirmed = Value(await Invoke(bridge, "sync:confirm-recovery-saved"));
		Assert.False(RecoveryPending(confirmed));
		Assert.False(PushedStates()[^1].RecoveryPending);
	}

	[Fact]
	public async Task ShowingTheRecoveryKeyAsksHelloAndACancelRevealsNothing()
	{
		var bridge = Api(Setup(new FakeIssuerSignIn(_server)), account: AccountWith(Strong));
		Value(await Invoke(bridge, "sync:sign-in"));
		var recovery = Value(await Invoke(bridge, "sync:turn-on", Strong)).GetProperty("recoveryKey").GetString()!;
		_vault.ForgetSecretKey();
		_sealer.Result = HelloResult.Cancel;

		var cancelled = await Invoke(bridge, "sync:show-recovery");

		Assert.False(cancelled.GetProperty("ok").GetBoolean());
		Assert.Equal(nameof(HelloCancelledException), cancelled.GetProperty("error").GetProperty("name").GetString());
		Assert.DoesNotContain(recovery, cancelled.ToString());
		Assert.DoesNotContain(recovery.Replace("-", ""), cancelled.ToString());

		_sealer.Result = HelloResult.Pass;
		Assert.Equal(recovery, Value(await Invoke(bridge, "sync:show-recovery")).GetProperty("recoveryKey").GetString());
	}

	[Fact]
	public async Task AWeakPasswordIsRefusedWithItsOwnError()
	{
		var bridge = Api(Setup(new FakeIssuerSignIn(_server)), account: AccountWith("correct horse"));
		Value(await Invoke(bridge, "sync:sign-in"));

		var refused = await Invoke(bridge, "sync:turn-on", "correct horse");

		Assert.Equal(nameof(SyncPasswordTooWeakException), refused.GetProperty("error").GetProperty("name").GetString());
		Assert.False(Value(await Invoke(bridge, "sync:get")).GetProperty("on").GetBoolean());
	}

	[Fact]
	public async Task ChangingThePasswordOverTheBridgeIsRefusedAsAnOutcomeWhenTheCurrentOneIsWrong()
	{
		var account = AccountWith("correct horse");
		var bridge = Api(Setup(new FakeIssuerSignIn(_server)), account: account);

		var refused = Value(await Invoke(bridge, "account:change-password", "wrong password", Strong));
		Assert.Equal("rejected", refused.GetProperty("status").GetString());
		Assert.Equal("wrong-password", refused.GetProperty("code").GetString());

		Assert.Equal("ok", Value(await Invoke(bridge, "account:change-password", "correct horse", Strong)).GetProperty("status").GetString());
		account.CheckPassword(Strong);
	}

	[Fact]
	public async Task SignedOutTheRecoveryKeyIsStillAskedForUntilItIsConfirmedSaved()
	{
		var bridge = Api(Setup(new FakeIssuerSignIn(_server)), account: AccountWith(Strong));
		Value(await Invoke(bridge, "sync:sign-in"));
		Value(await Invoke(bridge, "sync:turn-on", Strong));

		var signedOut = Value(await Invoke(bridge, "sync:sign-out"));

		Assert.False(signedOut.GetProperty("on").GetBoolean());
		Assert.True(RecoveryPending(signedOut));
		Assert.True(RecoveryPending(Value(await Invoke(bridge, "sync:get"))));
	}

	/// <summary>A vault that follows passwordAutofill, in its own folder, as Zing builds it (Program.OpenVault).</summary>
	private sealed class Switched : IDisposable
	{
		public string Dir { get; } = Directory.CreateTempSubdirectory("zing-syncbridge-switched-").FullName;
		public SettingsStore Settings { get; } = new();
		public VaultSwitch Vaults { get; }

		public Switched(FakeSealer sealer, bool on = true)
		{
			Settings.Set("passwordAutofill", on);
			Vaults = new VaultSwitch(Settings, () => new VaultStore(Dir, new FakeCipher(), sealer, new TickingClock(), new ManualScheduler(), new FakeSessionEvents()),
				e => throw new Xunit.Sdk.XunitException($"the vault couldn't be read: {e}"), () => File.Exists(Path.Combine(Dir, VaultStore.VaultFileName)),
				() => VaultKeys.SyncOnDisk(Path.Combine(Dir, VaultStore.KeysFileName)));
		}

		public void Dispose()
		{
			Vaults.Dispose();
			Directory.Delete(Dir, recursive: true);
		}
	}

	private Bridge Api(SyncSetup sync, Switched switched, AccountStore account)
	{
		var bridge = new Bridge();
		_apis.Add(new ChromeApi(bridge, new AppServices(switched.Settings, _library, Address.FromDefaults(), new TickingClock(), account, new RelayConsent(), Switch: switched.Vaults, Sync: sync), _host, "test"));
		return bridge;
	}

	[Fact]
	public async Task WithTheVaultOffAndSyncOnThePasswordChangeAsksForTheVaultFirst()
	{
		using var switched = new Switched(_sealer);
		var account = AccountWith(Strong);
		var bridge = Api(Setup(new FakeIssuerSignIn(_server)), switched, account);
		Value(await Invoke(bridge, "sync:sign-in"));
		Value(await Invoke(bridge, "sync:turn-on", Strong));
		switched.Settings.Set("passwordAutofill", false);

		var refused = await Invoke(bridge, "account:change-password", Strong, "labour-muzzle-medici-rushes-fiction");

		Assert.Equal(nameof(SyncNeedsTheVaultException), refused.GetProperty("error").GetProperty("name").GetString());
		account.CheckPassword(Strong);
	}

	[Fact]
	public async Task WithTheVaultOffAndSyncNeverOnThePasswordChangesHere()
	{
		using var switched = new Switched(_sealer);
		var account = AccountWith(Strong);
		var bridge = Api(Setup(new FakeIssuerSignIn(_server)), switched, account);
		await switched.Vaults.Current!.SaveAsync(new NewLogin("https://bank.example.test", "ada", "pw", "", ""));
		switched.Settings.Set("passwordAutofill", false);

		Assert.Equal("ok", Value(await Invoke(bridge, "account:change-password", Strong, "labour-muzzle-medici-rushes-fiction")).GetProperty("status").GetString());
		account.CheckPassword("labour-muzzle-medici-rushes-fiction");
	}

	[Fact]
	public async Task TurningTheVaultOffDisposesItsSyncClient()
	{
		using var switched = new Switched(_sealer);
		var app = new AppServices(switched.Settings, _library, Address.FromDefaults(), new TickingClock(), Accounts.Guest(), new RelayConsent(), Switch: switched.Vaults, Sync: Setup(new FakeIssuerSignIn(_server)));
		var first = app.SyncClients!.For(switched.Vaults.Current!);

		switched.Settings.Set("passwordAutofill", false);

		await Assert.ThrowsAsync<ObjectDisposedException>(() => first.SignOutAsync(TestContext.Current.CancellationToken));
		switched.Settings.Set("passwordAutofill", true);
		Assert.NotSame(first, app.SyncClients.For(switched.Vaults.Current!));
		app.SyncClients.Dispose();
	}

	[Fact]
	public async Task OnAFreshProfileRestoreFromSyncTurnsTheVaultOnAndMakesTheAccountWithThePassword()
	{
		var a = Api(Setup(new FakeIssuerSignIn(_server)), account: AccountWith(Strong));
		await _vault.SaveAsync(new NewLogin("https://bank.example.test", "ada", "bank-secret", "Bank", ""));
		Value(await Invoke(a, "sync:sign-in"));
		Value(await Invoke(a, "sync:turn-on", Strong));
		using var fresh = new Switched(_sealer, on: false);
		var account = new AccountStore(Directory.CreateTempSubdirectory("zing-syncbridge-fresh-").FullName, new FakeCipher());
		var b = Api(Setup(new FakeIssuerSignIn(_server)), fresh, account);

		var signedIn = Value(await Invoke(b, "sync:restore-sign-in"));
		var wrong = Value(await Invoke(b, "sync:restore-password", "labour-muzzle-medici-rushes-fiction"));
		Assert.False(account.State().HasAccount);
		var restored = Value(await Invoke(b, "sync:restore-password", Strong));

		Assert.True(signedIn.GetProperty("signedIn").GetBoolean());
		Assert.True(fresh.Settings.Get("passwordAutofill")!.GetValue<bool>());
		Assert.Equal("wrong-password", wrong.GetProperty("code").GetString());
		Assert.Equal("ok", restored.GetProperty("status").GetString());
		Assert.True(restored.GetProperty("state").GetProperty("signedIn").GetBoolean());
		account.CheckPassword(Strong);
		Assert.Equal(_vault.VaultId, fresh.Vaults.Current!.VaultId);
	}

	[Fact]
	public async Task ARecoveryKeyRestoreWaitsForANewPasswordBeforeTheAccountIsMade()
	{
		var a = Api(Setup(new FakeIssuerSignIn(_server)), account: AccountWith(Strong));
		Value(await Invoke(a, "sync:sign-in"));
		var recovery = Value(await Invoke(a, "sync:turn-on", Strong)).GetProperty("recoveryKey").GetString()!;
		using var fresh = new Switched(_sealer, on: false);
		var account = new AccountStore(Directory.CreateTempSubdirectory("zing-syncbridge-fresh-").FullName, new FakeCipher());
		var b = Api(Setup(new FakeIssuerSignIn(_server)), fresh, account);
		Value(await Invoke(b, "sync:restore-sign-in"));

		var mistyped = await Invoke(b, "sync:restore-recovery-key", recovery[..^1]);
		Assert.True((await Invoke(b, "sync:restore-recovery-key", recovery)).GetProperty("ok").GetBoolean());
		Assert.False(account.State().HasAccount);
		var set = Value(await Invoke(b, "sync:restore-new-password", "labour-muzzle-medici-rushes-fiction"));

		Assert.Equal(nameof(RecoveryKeyFormatException), mistyped.GetProperty("error").GetProperty("name").GetString());
		Assert.Equal("ok", set.GetProperty("status").GetString());
		account.CheckPassword("labour-muzzle-medici-rushes-fiction");
	}

	[Fact]
	public async Task SigningInAfterAPasswordChangedElsewhereChecksTheNewPasswordByUnwrapping()
	{
		var account = AccountWith(Strong);
		var bridge = Api(Setup(new FakeIssuerSignIn(_server)), account: account);
		Value(await Invoke(bridge, "sync:sign-in"));
		Value(await Invoke(bridge, "sync:turn-on", Strong));
		var stored = _vault.SyncKeys!;
		using (var keys = await _vault.KeyMaterialAsync())
		{
			var newer = SyncKeysBlob.Parse(stored.Blob).WithPassword(keys, "labour-muzzle-medici-rushes-fiction").ToBytes();
			_vault.MarkPasswordChangedElsewhere(newer, stored.Version + 1);
		}
		SyncRestore.AskIfPasswordChangedElsewhere(account, _vault);

		var old = Value(await Invoke(bridge, "account:sign-in", Strong));
		var signedIn = Value(await Invoke(bridge, "account:sign-in", "labour-muzzle-medici-rushes-fiction"));

		Assert.Equal("wrong-password", old.GetProperty("code").GetString());
		Assert.Equal("ok", signedIn.GetProperty("status").GetString());
		Assert.False(signedIn.GetProperty("state").GetProperty("passwordChangedElsewhere").GetBoolean());
		account.CheckPassword("labour-muzzle-medici-rushes-fiction");
		Assert.Equal(stored.Version + 1, _vault.SyncKeys!.Version);
	}

	[Fact]
	public async Task WithSyncOnAPasswordResetNeedsTheRecoveryKey()
	{
		var account = AccountWith(Strong);
		var bridge = Api(Setup(new FakeIssuerSignIn(_server)), account: account);
		Value(await Invoke(bridge, "sync:sign-in"));
		Value(await Invoke(bridge, "sync:turn-on", Strong));

		var refused = await Invoke(bridge, "account:reset-password", "labour-muzzle-medici-rushes-fiction");

		Assert.Equal(nameof(SyncNeedsRecoveryKeyException), refused.GetProperty("error").GetProperty("name").GetString());
		account.CheckPassword(Strong);
	}

	[Fact]
	public async Task APasswordChangeInOneWindowWaitsForATurnOnInAnotherAndIsSynced()
	{
		var account = AccountWith(Strong);
		var app = new AppServices(new SettingsStore(), _library, Address.FromDefaults(), new TickingClock(), account, new RelayConsent(), Vault: _vault, Sync: Setup(new FakeIssuerSignIn(_server)));
		var first = new Bridge();
		var second = new Bridge();
		_apis.Add(new ChromeApi(first, app, _host, "first"));
		_apis.Add(new ChromeApi(second, app, _host, "second"));
		Value(await Invoke(first, "sync:sign-in"));
		var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var held = 0;
		_sealer.Prompting = async () =>
		{
			if (Interlocked.Exchange(ref held, 1) == 1) return;
			reached.SetResult();
			await release.Task;
		};
		var turnOn = Task.Run(() => Invoke(first, "sync:turn-on", Strong), TestContext.Current.CancellationToken);
		await reached.Task;

		var change = Task.Run(() => Invoke(second, "account:change-password", Strong, "labour-muzzle-medici-rushes-fiction"), TestContext.Current.CancellationToken);
		await SyncKeys.QueuedBehind(_vault).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
		Assert.False(change.IsCompleted);
		release.SetResult();
		Value(await turnOn);

		Assert.Equal("ok", Value(await change).GetProperty("status").GetString());
		account.CheckPassword("labour-muzzle-medici-rushes-fiction");
		var client = app.SyncClients!.For(_vault);
		using var opened = SyncKeysBlob.Parse((await client.GetKeysAsync(TestContext.Current.CancellationToken))!.Blob).UnwrapWithPassword("labour-muzzle-medici-rushes-fiction");
		app.SyncClients.Dispose();
	}
}
