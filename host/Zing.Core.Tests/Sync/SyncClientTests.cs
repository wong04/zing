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
	public async Task ARefusedRefreshRaisesItsStatus()
	{
		var store = new MemoryAccountStore();
		store.SaveSyncAccount(new SyncAccount("user", LocalSyncServer.Email, "not-a-refresh-token"));
		using var client = Client(store);

		var refused = await Assert.ThrowsAsync<SyncHttpException>(() => client.GetKeysAsync(Ct));

		Assert.Equal(HttpStatusCode.Unauthorized, refused.Status);
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
