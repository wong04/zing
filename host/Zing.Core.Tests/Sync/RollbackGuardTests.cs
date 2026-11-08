using System.Text.Json;
using System.Text.Json.Nodes;
using Zing.Core.Net;
using Zing.Core.Sync;
using Zing.Core.Tests.Vault;
using Zing.Core.Vault;

namespace Zing.Core.Tests.Sync;

/// <summary>Rollback protection (spec): the floors themselves, then a server that goes back, replays an old copy, or holds an item back.</summary>
public sealed class RollbackGuardTests : IDisposable
{
	private readonly TwoPcs _pcs = new();

	public void Dispose() => _pcs.Dispose();

	private static CancellationToken Ct => TestContext.Current.CancellationToken;

	private SyncPc A => _pcs.A;

	private SyncPc B => _pcs.B;

	[Fact]
	public void ALowerServerRevisionIsARollback()
	{
		RollbackGuard.CheckRevision(5, 5);
		RollbackGuard.CheckRevision(5, 6);
		Assert.Throws<ServerRolledBackException>(() => RollbackGuard.CheckRevision(5, 4));
	}

	[Fact]
	public void AWriteGivenARevisionAlreadySeenIsARollback()
	{
		RollbackGuard.CheckAccepted(5, [new SyncAccepted("a", 6)]);
		Assert.Throws<ServerRolledBackException>(() => RollbackGuard.CheckAccepted(5, [new SyncAccepted("a", 5)]));
	}

	[Fact]
	public void AnOlderManifestOrNoneAfterOneWasSeenIsARollback()
	{
		RollbackGuard.CheckManifest(0, null);
		RollbackGuard.CheckManifest(3, 3);
		Assert.Throws<ServerRolledBackException>(() => RollbackGuard.CheckManifest(3, 2));
		Assert.Throws<ServerRolledBackException>(() => RollbackGuard.CheckManifest(3, null));
	}

	[Fact]
	public void AnItemBelowThisPcsFloorIsARollbackAndALaterGenerationIsNot()
	{
		RollbackGuard.CheckItem("a", new ItemOrder(0, 4), null);
		RollbackGuard.CheckItem("a", new ItemOrder(0, 4), new ItemOrder(0, 4));
		RollbackGuard.CheckItem("a", new ItemOrder(1, 2), new ItemOrder(0, 9));
		Assert.Throws<ServerRolledBackException>(() => RollbackGuard.CheckItem("a", new ItemOrder(0, 3), new ItemOrder(0, 4)));
		Assert.Throws<ServerRolledBackException>(() => RollbackGuard.CheckItem("a", new ItemOrder(0, 9), new ItemOrder(1, 2)));
	}

	[Fact]
	public void AListedItemTheServerDidNotSendIsWithheld()
	{
		var known = new Dictionary<string, long> { ["a"] = 3 };
		RollbackGuard.CheckListed([new SyncManifestEntry("a", 3)], id => known.TryGetValue(id, out var v) ? v : null);
		var older = Assert.Throws<ServerWithheldItemException>(() => RollbackGuard.CheckListed([new SyncManifestEntry("a", 4)], id => known.TryGetValue(id, out var v) ? v : null));
		Assert.Equal("a", older.ItemId);
		Assert.Throws<ServerWithheldItemException>(() => RollbackGuard.CheckListed([new SyncManifestEntry("b", 1)], id => known.TryGetValue(id, out var v) ? v : null));
	}

	[Fact]
	public async Task TheServerServingAnOlderCopyOfALoginIsRefused()
	{
		var id = await _pcs.SaveOnAAsync();
		await _pcs.TurnOnAAsync();
		await _pcs.JoinBAsync();
		var old = (await _pcs.ServerItemsAsync()).Items.Single(i => i.ItemId == id);
		A.Vault.UpdateSummary(id, "Newer", "", "ada");
		await A.SyncAsync();
		await B.SyncAsync();
		A.Vault.UpdateSummary(id, "Newest", "", "ada");
		await A.SyncAsync();
		// The server swaps the newest copy for the first one, still sealed and tagged as it was: a replay.
		_pcs.Server.Wire.Rewrite = (line, body) => line == "GET /changes" ? ReplaceItem(body, id, old) : body;

		await Assert.ThrowsAsync<ServerRolledBackException>(B.SyncAsync);

		Assert.Equal("Newer", B.Login(id).Title);
		Assert.Equal("server-rolled-back", B.Engine.Status.Error);
	}

	[Fact]
	public async Task AServerRestoredFromABackupRaisesRolledBackAndUploadThisPcsCopyPutsItBack()
	{
		var id = await _pcs.SaveOnAAsync();
		await _pcs.TurnOnAAsync();
		var backup = _pcs.Server.Backup();
		A.Vault.UpdateSummary(id, "After the backup", "", "ada");
		await _pcs.SaveOnAAsync("https://shop.example.test");
		await A.SyncAsync();
		_pcs.Server.Restore(backup);

		await Assert.ThrowsAsync<ServerRolledBackException>(A.SyncAsync);
		Assert.Equal("server-rolled-back", A.Engine.Status.Error);

		await A.Engine.UploadLocalCopyAsync(Ct);

		Assert.Null(A.Engine.Status.Error);
		Assert.Empty(A.Vault.Unsent());
		await A.SyncAsync();
		await _pcs.JoinBAsync();
		Assert.Equal("After the backup", B.Login(id).Title);
		Assert.Equal(2, B.Vault.Summaries().Count);
	}

	[Fact]
	public async Task AServerHoldingBackALoginItsManifestListsIsCaught()
	{
		var id = await _pcs.SaveOnAAsync();
		await _pcs.SaveOnAAsync("https://shop.example.test");
		await _pcs.TurnOnAAsync();
		_pcs.Server.Wire.Rewrite = (line, body) => line == "GET /changes" ? RemoveItem(body, id) : body;

		var withheld = await Assert.ThrowsAsync<ServerWithheldItemException>(_pcs.JoinBAsync);

		Assert.Equal(id, withheld.ItemId);
		Assert.Equal("server-withheld-item", B.Engine.Status.Error);
	}

	[Fact]
	public async Task ARollbackReachesSettingsAsAnErrorThatOffersUploadThisPcsCopy()
	{
		var vaultDir = Directory.CreateTempSubdirectory("zing-rollback-ui-").FullName;
		try
		{
			var scheduler = new TestScheduler();
			var vault = new VaultStore(vaultDir, new FakeCipher(), new FakeSealer(), new SettableClock(), new ManualScheduler(), new FakeSessionEvents());
			var host = new FakeWindowHost();
			var library = new LibraryStore(Library.EmptyLibrary(0), _ => { }, Deterministic.Clock(), Deterministic.Ids("id-"));
			var app = new AppServices(new SettingsStore(), library, Address.FromDefaults(), new SettableClock(), _pcs.Account, new RelayConsent(), Vault: vault, Sync: new SyncSetup(_pcs.Server.Address, new FakeIssuer(_pcs.Server), _pcs.Server.Handler()), SyncScheduler: scheduler);
			var bridge = new Bridge();
			using var api = new ChromeApi(bridge, app, host, "test");
			await vault.SaveAsync(new NewLogin("https://bank.example.test", "ada", "pw", "Bank", ""));
			Value(await Invoke(bridge, "sync:sign-in"));
			Value(await Invoke(bridge, "sync:turn-on", TwoPcs.Strong));
			var engine = app.SyncClients!.Engine!;
			await engine.SyncAsync(Ct);
			var backup = _pcs.Server.Backup();
			await vault.SaveAsync(new NewLogin("https://shop.example.test", "ada", "pw", "Shop", ""));
			await engine.SyncAsync(Ct);
			_pcs.Server.Restore(backup);

			await Assert.ThrowsAsync<ServerRolledBackException>(() => engine.SyncAsync(Ct));

			var pushed = host.Pushed.Where(p => p.Channel == "sync:state").Select(p => (SyncState)p.Payload!).Last();
			Assert.Equal("server-rolled-back", pushed.Error);
			Assert.Contains("Upload this PC's copy", pushed.Message);
			var state = Value(await Invoke(bridge, "sync:get"));
			Assert.Equal("server-rolled-back", state.GetProperty("error").GetString());

			var uploaded = Value(await Invoke(bridge, "sync:upload-local"));
			Assert.False(uploaded.TryGetProperty("error", out _));
			Assert.Equal(0, uploaded.GetProperty("unsent").GetInt32());
			app.SyncClients.Dispose();
			vault.Dispose();
		}
		finally
		{
			Directory.Delete(vaultDir, recursive: true);
		}
	}

	private sealed class FakeIssuer(LocalSyncServer server) : IGoogleSignIn
	{
		public Task<string> SignInAsync(CancellationToken cancellation) => server.MintIdTokenAsync();
	}

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

	/// <summary>The page with <paramref name="id"/>'s copy swapped for <paramref name="old"/>, at the revision the page gives it.</summary>
	private static string ReplaceItem(string body, string id, SyncItem old)
	{
		var page = JsonNode.Parse(body)!;
		foreach (var item in page["items"]!.AsArray())
		{
			if (item!["itemId"]!.GetValue<string>() != id) continue;
			var replayed = JsonSerializer.SerializeToNode(old with { Revision = item["revision"]!.GetValue<long>() }, SyncJson.Options)!;
			foreach (var (key, value) in replayed.AsObject()) item[key] = value?.DeepClone();
		}
		return page.ToJsonString();
	}

	private static string RemoveItem(string body, string id)
	{
		var page = JsonNode.Parse(body)!;
		var items = page["items"]!.AsArray();
		foreach (var item in items.Where(i => i!["itemId"]!.GetValue<string>() == id).ToList()) items.Remove(item);
		return page.ToJsonString();
	}
}
