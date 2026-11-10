using System.Net;
using System.Text;
using Zing.Core.Sync;

namespace Zing.Core.Tests.Sync;

/// <summary>The sync client against the real server (fake Google issuer): sign-in, the session's one refresh, and each endpoint's shape.</summary>
public sealed class SyncClientTests : IDisposable
{
	private readonly LocalSyncServer _server = new();

	public void Dispose() => _server.Dispose();

	private static CancellationToken Ct => TestContext.Current.CancellationToken;

	private SyncClient Client(ISyncAccountStore store) => new(_server.Address, store, _server.Handler());

	private async Task<(SyncClient Client, MemoryAccountStore Store)> SignedIn()
	{
		var store = new MemoryAccountStore(_server.Wire);
		var client = Client(store);
		await client.SignInAsync(await _server.MintIdTokenAsync(), Ct);
		return (client, store);
	}

	[Fact]
	public async Task SigningInStoresTheAccountWithItsEmailAndRefreshToken()
	{
		var store = new MemoryAccountStore();
		using var client = Client(store);

		var account = await client.SignInAsync(await _server.MintIdTokenAsync(), Ct);

		Assert.Equal(LocalSyncServer.Email, account.Email);
		Assert.NotEmpty(account.UserId);
		Assert.NotEmpty(account.Refresh);
		Assert.Equal(account, store.SyncAccount());
	}

	[Fact]
	public async Task ATokenTheServerRefusesRaisesItsStatusAndStoresNothing()
	{
		var store = new MemoryAccountStore();
		using var client = Client(store);

		var idToken = await _server.MintIdTokenAsync(sub: "sub-not-allowed");
		var refused = await Assert.ThrowsAsync<SyncHttpException>(() => client.SignInAsync(idToken, Ct));

		Assert.Equal(HttpStatusCode.Forbidden, refused.Status);
		Assert.Null(store.SyncAccount());
	}

	[Fact]
	public async Task A401RefreshesOnceStoresTheRotatedTokenThenRetries()
	{
		var (client, store) = await SignedIn();
		using var _ = client;
		var first = store.SyncAccount()!.Refresh;
		_server.Wire.Log.Clear();
		_server.Wire.SpoilNext = 1;

		Assert.Null(await client.GetKeysAsync(Ct));

		Assert.Equal(["GET /keys", "POST /auth/refresh", "save account", "GET /keys"], _server.Wire.Log);
		Assert.NotEqual(first, store.SyncAccount()!.Refresh);
	}

	[Fact]
	public async Task TheStoredTokenIsTheLiveOneAfterARotation()
	{
		var (client, store) = await SignedIn();
		using var _ = client;
		_server.Wire.SpoilNext = 1;
		await client.GetKeysAsync(Ct);

		// A fresh client has no session yet, so it refreshes with what was stored; reusing a rotated token would revoke the account.
		using var later = Client(store);
		Assert.Null(await later.GetManifestAsync(Ct));
		Assert.Equal(2, _server.Wire.Refreshes);
	}

	[Fact]
	public async Task ASecond401Raises()
	{
		var (client, _) = await SignedIn();
		using var __ = client;
		_server.Wire.SpoilAll = true;

		var refused = await Assert.ThrowsAsync<SyncHttpException>(() => client.GetKeysAsync(Ct));

		Assert.Equal(HttpStatusCode.Unauthorized, refused.Status);
		Assert.Equal(1, _server.Wire.Refreshes);
	}

	[Fact]
	public async Task CallsAtOnceShareOneRefresh()
	{
		var (client, store) = await SignedIn();
		using var _ = client;
		using var fresh = Client(store);

		await Task.WhenAll(fresh.GetKeysAsync(Ct), fresh.GetManifestAsync(Ct), fresh.GetChangesAsync(0, cancellation: Ct));

		Assert.Equal(1, _server.Wire.Refreshes);
	}

	[Fact]
	public async Task ARefreshTheServerRefusesSignsThisPcOutOnceWithoutALoop()
	{
		var store = new MemoryAccountStore();
		store.SaveSyncAccount(new SyncAccount("user", LocalSyncServer.Email, "not-a-refresh-token"));
		using var client = Client(store);

		await Assert.ThrowsAsync<SyncSignedOutException>(() => client.GetKeysAsync(Ct));
		await Assert.ThrowsAsync<NotSignedInToSyncException>(() => client.GetManifestAsync(Ct));

		Assert.Null(store.SyncAccount());
		Assert.True(store.SignedOutByServer);
		Assert.Equal(["POST /auth/refresh"], _server.Wire.Log);
	}

	[Fact]
	public async Task AServerThatCantBeReachedDuringARefreshKeepsTheAccount()
	{
		var (client, store) = await SignedIn();
		using var _ = client;
		using var fresh = Client(store);
		_server.Wire.Down = true;

		await Assert.ThrowsAsync<HttpRequestException>(() => fresh.GetKeysAsync(Ct));

		Assert.NotNull(store.SyncAccount());
		Assert.False(store.SignedOutByServer);
	}

	[Fact]
	public async Task ACancelOnceTheServerAnsweredStillStoresTheRotatedToken()
	{
		var (client, store) = await SignedIn();
		using var _ = client;
		var first = store.SyncAccount()!.Refresh;
		using var fresh = Client(store);
		using var cancel = new CancellationTokenSource();
		_server.Wire.AfterRefreshAnswered = cancel.Cancel;

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fresh.GetKeysAsync(cancel.Token));

		Assert.NotEqual(first, store.SyncAccount()!.Refresh);
		_server.Wire.AfterRefreshAnswered = null;
		using var later = Client(store);
		Assert.Null(await later.GetKeysAsync(Ct));
	}

	[Fact]
	public async Task ALostRefreshAnswerIsRetriedWithTheSameTokenAndTheAccountSurvives()
	{
		var (client, store) = await SignedIn();
		using var _ = client;
		using var fresh = Client(store);
		_server.Wire.LoseNextRefreshAnswer = true;

		await Assert.ThrowsAsync<HttpRequestException>(() => fresh.GetKeysAsync(Ct));
		Assert.Null(await fresh.GetKeysAsync(Ct));

		Assert.False(store.SignedOutByServer);
		Assert.Equal(2, _server.Wire.Refreshes);
	}

	[Fact]
	public async Task ASessionThisCallJustGotThatIsRefusedRaisesWithoutASecondRefresh()
	{
		var (client, store) = await SignedIn();
		using var _ = client;
		using var fresh = Client(store);
		_server.Wire.SpoilAll = true;

		var refused = await Assert.ThrowsAsync<SyncHttpException>(() => fresh.GetKeysAsync(Ct));

		Assert.Equal(HttpStatusCode.Unauthorized, refused.Status);
		Assert.Equal(1, _server.Wire.Refreshes);
	}

	[Fact]
	public async Task SigningOutRevokesTheSignInOnTheServerThenForgetsIt()
	{
		var (client, store) = await SignedIn();
		using var _ = client;
		var refresh = store.SyncAccount()!.Refresh;

		Assert.True(await client.SignOutAsync(Ct));

		Assert.Null(store.SyncAccount());
		using var raw = new HttpClient(_server.Handler(), disposeHandler: false) { BaseAddress = _server.Address };
		var reused = await raw.PostAsync("auth/refresh", System.Net.Http.Json.JsonContent.Create(new { refresh }), Ct);
		Assert.Equal(HttpStatusCode.Unauthorized, reused.StatusCode);
	}

	private async Task<HttpStatusCode> RefreshStatusAsync(string refresh)
	{
		using var raw = new HttpClient(_server.Handler(), disposeHandler: false) { BaseAddress = _server.Address };
		using var answer = await raw.PostAsync("auth/refresh", System.Net.Http.Json.JsonContent.Create(new { refresh }), Ct);
		return answer.StatusCode;
	}

	[Fact]
	public async Task SigningInAgainRevokesTheSignInItReplaces()
	{
		var (client, store) = await SignedIn();
		using var _ = client;
		var replaced = store.SyncAccount()!.Refresh;

		await client.SignInAsync(await _server.MintIdTokenAsync(), Ct);
		await client.Revoking;

		Assert.NotEqual(replaced, store.SyncAccount()!.Refresh);
		Assert.Equal(HttpStatusCode.Unauthorized, await RefreshStatusAsync(replaced));
	}

	[Fact]
	public async Task AStalledRevokeDoesntHoldUpTheSignInThatReplacedIt()
	{
		var (client, store) = await SignedIn();
		using var _ = client;
		var replaced = store.SyncAccount()!.Refresh;
		var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		_server.Wire.BeforeSend = async line =>
		{
			if (line == "POST /auth/signout") await held.Task;
		};

		var signIn = client.SignInAsync(await _server.MintIdTokenAsync(), Ct);
		Task first;
		try
		{
			first = await Task.WhenAny(signIn, Task.Delay(TimeSpan.FromSeconds(10), Ct));
		}
		finally
		{
			held.SetResult();
		}

		Assert.Same(signIn, first);
		await client.Revoking;
		Assert.Equal(HttpStatusCode.Unauthorized, await RefreshStatusAsync(replaced));
	}

	/// <summary>
	/// A test can end while a replaced sign-in's revoke is still in the server, holding zing.db open: disposing the server deleted the
	/// folder under it ("used by another process"). The server waits for every request on its wire before it goes.
	/// </summary>
	[Fact]
	public async Task DisposingTheServerWaitsForARevokeStillOnTheWire()
	{
		var server = new LocalSyncServer();
		var warnings = new List<Exception>();
		using var client = new SyncClient(server.Address, new MemoryAccountStore(), server.Handler(), (_, error) => warnings.Add(error));
		await client.SignInAsync(await server.MintIdTokenAsync(), Ct);
		var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		server.Wire.BeforeSend = async line =>
		{
			if (line != "POST /auth/signout") return;
			reached.SetResult();
			await held.Task;
		};
		await client.SignInAsync(await server.MintIdTokenAsync(), Ct);
		await reached.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

		var disposing = server.DisposeAsync().AsTask();
		held.SetResult();
		await disposing;
		await client.Revoking;

		Assert.Empty(warnings);
	}

	[Fact]
	public async Task AdoptingASignInRevokesTheOneItReplacesAndSendsAsTheAdoptedOne()
	{
		var (client, store) = await SignedIn();
		using var _ = client;
		var replaced = store.SyncAccount()!.Refresh;
		var (other, otherStore) = await SignedIn();
		using var __ = other;
		var adopted = otherStore.SyncAccount()!;

		await client.AdoptAsync(adopted, Ct);
		await client.Revoking;

		Assert.Equal(adopted, store.SyncAccount());
		Assert.Equal(HttpStatusCode.Unauthorized, await RefreshStatusAsync(replaced));
		Assert.Null(await client.GetKeysAsync(Ct));
		Assert.Equal(1, _server.Wire.Count("POST /auth/signout"));
	}

	[Fact]
	public async Task SigningOutWithTheServerDownStillForgetsTheAccount()
	{
		var (client, store) = await SignedIn();
		using var _ = client;
		_server.Wire.Down = true;

		Assert.False(await client.SignOutAsync(Ct));

		Assert.Null(store.SyncAccount());
	}

	[Fact]
	public async Task WithoutAnAccountACallIsRefusedBeforeAnyRequest()
	{
		using var client = Client(new MemoryAccountStore());

		await Assert.ThrowsAsync<NotSignedInToSyncException>(() => client.GetKeysAsync(Ct));

		Assert.Empty(_server.Wire.Log);
	}

	[Fact]
	public async Task KeysAndManifestAreVersionedAndAStaleWriteRaisesTheCurrentCopy()
	{
		var (client, _) = await SignedIn();
		using var __ = client;
		var keys = Encoding.UTF8.GetBytes("wrapped keys");

		Assert.Equal(1, await client.PutKeysAsync(keys, 0, Ct));
		AssertBlob(keys, 1, await client.GetKeysAsync(Ct));
		var stale = await Assert.ThrowsAsync<SyncConflictException>(() => client.PutKeysAsync(Encoding.UTF8.GetBytes("other"), 0, Ct));
		Assert.Equal(HttpStatusCode.Conflict, stale.Status);
		AssertBlob(keys, 1, stale.Current);

		var manifest = Encoding.UTF8.GetBytes("manifest");
		Assert.Equal(1, await client.PutManifestAsync(manifest, 0, Ct));
		AssertBlob(manifest, 1, await client.GetManifestAsync(Ct));
	}

	private static void AssertBlob(byte[] blob, long version, VersionedBlob? actual)
	{
		Assert.NotNull(actual);
		Assert.Equal(blob, actual.Blob);
		Assert.Equal(version, actual.Version);
	}

	[Fact]
	public async Task ChangesGoUpAndComeBackWithTheirRevisions()
	{
		var (client, _) = await SignedIn();
		using var __ = client;
		var item = new SyncPush("item-1", 1, 0, false, [1, 2, 3], [4, 5], []);

		var pushed = await client.PostChangesAsync([item], Ct);
		var conflicted = await client.PostChangesAsync([item], Ct);
		var page = await client.GetChangesAsync(0, cancellation: Ct);

		Assert.Equal([new SyncAccepted("item-1", 1)], pushed.Accepted);
		Assert.Empty(pushed.Conflicts);
		Assert.Equal("item-1", Assert.Single(conflicted.Conflicts).ItemId);
		var pulled = Assert.Single(page.Items);
		Assert.Equal((1L, 1), (pulled.Revision, pulled.Version));
		Assert.Equal([1, 2, 3], pulled.Summary);
		Assert.Equal([4, 5], pulled.Password);
		Assert.Equal(1, page.Revision);
		Assert.False(page.More);
	}
}
