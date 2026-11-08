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
}
