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

	public SyncBridgeTests() =>
		_vault = new VaultStore(_dir, new FakeCipher(), new FakeSealer(), new TickingClock(), new ManualScheduler(), new FakeSessionEvents());

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

	private Bridge Api(SyncSetup? sync, VaultStore? vault = null)
	{
		var bridge = new Bridge();
		_apis.Add(new ChromeApi(bridge, new AppServices(new SettingsStore(), _library, Address.FromDefaults(), new TickingClock(), Accounts.Guest(), new RelayConsent(), Vault: vault ?? _vault, Sync: sync), _host, "test"));
		return bridge;
	}

	private List<SyncState> PushedStates() => [.. _host.Pushed.Where(p => p.Channel == "sync:state").Select(p => (SyncState)p.Payload!)];

	private static string? Notice(JsonElement state) => state.TryGetProperty("notice", out var notice) ? notice.GetString() : null;

	private SyncSetup Setup(IGoogleSignIn signIn) => new(_server.Address, signIn, _server.Handler());

	private static async Task<JsonElement> Invoke(Bridge bridge, string channel)
	{
		var json = JsonSerializer.Serialize(new { kind = "invoke", id = 1, channel, args = Array.Empty<object>() });
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
}
