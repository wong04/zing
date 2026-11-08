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
	public async Task ARevisionZeroItemInAPullIsRefusedAndTheLoginStays()
	{
		var id = await _pcs.SaveOnAAsync();
		await _pcs.TurnOnAAsync();
		await _pcs.JoinBAsync();
		var held = (await _pcs.ServerItemsAsync()).Items.Single(i => i.ItemId == id);
		// Ruling 18's purged placeholder, slipped into a pull, where it is never valid.
		_pcs.Server.Wire.Rewrite = (line, body) => line == "GET /changes" ? AddItem(body, "items", held with { Revision = 0, Tombstone = true, Summary = [], Password = [], History = [] }) : body;

		await Assert.ThrowsAsync<SyncProtocolException>(B.SyncAsync);

		Assert.Equal("server-invalid", B.Engine.Status.Error);
		Assert.Equal("Bank", B.Login(id).Title);
		Assert.Equal("first-password", await B.Vault.PasswordAsync(id));
	}

	[Fact]
	public async Task AnItemPulledAtARevisionThePageDoesNotReachIsRefused()
	{
		var id = await _pcs.SaveOnAAsync();
		await _pcs.TurnOnAAsync();
		await _pcs.JoinBAsync();
		var held = (await _pcs.ServerItemsAsync()).Items.Single(i => i.ItemId == id);
		_pcs.Server.Wire.Rewrite = (line, body) => line == "GET /changes" ? AddItem(body, "items", held with { Revision = 1_000 }) : body;

		await Assert.ThrowsAsync<SyncProtocolException>(B.SyncAsync);

		Assert.Equal("server-invalid", B.Engine.Status.Error);
	}

	[Fact]
	public async Task APageThatSaysMoreButHoldsNothingIsRefused()
	{
		await _pcs.SaveOnAAsync();
		await _pcs.TurnOnAAsync();
		await _pcs.JoinBAsync();
		_pcs.Server.Wire.Rewrite = (line, body) => line == "GET /changes" ? SetMore(body) : body;

		await Assert.ThrowsAsync<SyncProtocolException>(B.SyncAsync);

		Assert.Equal("server-invalid", B.Engine.Status.Error);
	}

	[Fact]
	public async Task ARevisionZeroConflictForALoginThatWasNotPushedIsRefusedAndTheLoginStays()
	{
		var id = await _pcs.SaveOnAAsync();
		var other = await _pcs.SaveOnAAsync("https://shop.example.test");
		await _pcs.TurnOnAAsync();
		var held = (await _pcs.ServerItemsAsync()).Items.Single(i => i.ItemId == other);
		A.Vault.UpdateSummary(id, "Edited", "", "ada");
		_pcs.Server.Wire.Rewrite = (line, body) => line == "POST /changes" ? AddItem(body, "conflicts", held with { Revision = 0, Tombstone = true, Summary = [], Password = [], History = [] }) : body;

		await Assert.ThrowsAsync<SyncProtocolException>(A.SyncAsync);

		Assert.Equal("server-invalid", A.Engine.Status.Error);
		Assert.Equal("Bank", A.Login(other).Title);
		Assert.Equal("first-password", await A.Vault.PasswordAsync(other));
	}

	[Fact]
	public async Task AForcedResyncThatLeavesOutALoginNoManifestListedYetRaisesWithheldAndKeepsIt()
	{
		await _pcs.SaveOnAAsync();
		await _pcs.TurnOnAAsync();
		// A new login reaches the server, but the server refuses the manifest that would list it.
		var fresh = await _pcs.SaveOnAAsync("https://shop.example.test");
		_pcs.Server.Wire.AnswerWith = request => request.Method == HttpMethod.Put && request.RequestUri!.AbsolutePath == "/manifest" ? System.Net.HttpStatusCode.InternalServerError : null;
		await Assert.ThrowsAsync<SyncHttpException>(A.SyncAsync);
		Assert.Empty(A.Vault.Unsent());
		// Then it claims A's cursor is past its horizon, and serves the full resync without that login.
		_pcs.Server.Wire.AnswerWith = request => request.RequestUri!.AbsolutePath == "/changes" && request.Method == HttpMethod.Get && !request.RequestUri.Query.Contains("since=0") ? System.Net.HttpStatusCode.Gone : null;
		_pcs.Server.Wire.Rewrite = (line, body) => line == "GET /changes" ? RemoveItem(body, fresh) : body;

		var withheld = await Assert.ThrowsAsync<ServerWithheldItemException>(A.SyncAsync);

		Assert.Equal(fresh, withheld.ItemId);
		Assert.Equal("first-password", await A.Vault.PasswordAsync(fresh));
		Assert.Contains(A.Vault.Summaries(), s => s.Id == fresh && s.DeletedAt is null);
	}

	[Fact]
	public async Task AServerFlaggingADeletedLoginAsATombstoneIsCaughtAndTheLoginStaysInRecentlyDeleted()
	{
		var id = await _pcs.SaveOnAAsync();
		await _pcs.TurnOnAAsync();
		await _pcs.JoinBAsync();
		A.Vault.Delete(id);
		await A.SyncAsync();
		// Ruling 25: the flag is outside every ciphertext's associated data, and the sealed summary isn't a tombstone.
		_pcs.Server.Wire.Rewrite = (line, body) => line == "GET /changes" ? FlagTombstone(body, id) : body;

		await Assert.ThrowsAsync<VaultTamperedException>(B.SyncAsync);

		Assert.Equal("server-tampered", B.Engine.Status.Error);
		Assert.Contains(B.Vault.Summaries(), s => s.Id == id);
	}

	[Fact]
	public async Task AServerSwappingAMergedPasswordWithAnOldOneOfTheSameVersionIsCaught()
	{
		var id = await _pcs.SaveOnAAsync();
		await _pcs.TurnOnAAsync();
		await _pcs.JoinBAsync();
		await A.Vault.UpdatePasswordAsync(id, "changed-on-a");
		B.Clock.Advance(TimeSpan.FromMinutes(1));
		await B.Vault.UpdatePasswordAsync(id, "changed-on-b-later");
		await A.SyncAsync();
		await B.SyncAsync();
		// B's merge keeps both version-2 passwords: B's as the password, A's as the newest old one. Their associated data is the same.
		_pcs.Server.Wire.Rewrite = (line, body) => line == "GET /changes" ? SwapPasswordWithNewestOldOne(body, id) : body;

		await Assert.ThrowsAsync<VaultTamperedException>(A.SyncAsync);

		Assert.Equal("server-tampered", A.Engine.Status.Error);
		Assert.Equal("changed-on-a", await A.Vault.PasswordAsync(id));
	}

	[Fact]
	public async Task APushAnswerThatBothAcceptsALoginAndSaysItWasPurgedIsRefusedAndTheLoginStays()
	{
		var id = await _pcs.SaveOnAAsync();
		await _pcs.TurnOnAAsync();
		A.Vault.UpdateSummary(id, "Edited", "", "ada");
		var placeholder = new SyncItem(id, 0, 0, true, [], [], []);
		_pcs.Server.Wire.Rewrite = (line, body) => line == "POST /changes" ? AddItem(body, "conflicts", placeholder) : body;

		await Assert.ThrowsAsync<SyncProtocolException>(A.SyncAsync);

		Assert.Equal("server-invalid", A.Engine.Status.Error);
		Assert.Equal("Edited", A.Login(id).Title);
		Assert.Equal("first-password", await A.Vault.PasswordAsync(id));
		Assert.Single(A.Vault.Unsent());
	}

	/// <summary>
	/// A saves login X after a backup, syncs it and lists it; the server is restored, so it loses X. B, which never had X, then
	/// edits another login <paramref name="writesOnB"/> times, writing a manifest from its own view each time. Returns X.
	/// </summary>
	private async Task<string> ListedLoginLostInARestoreAsync(int writesOnB)
	{
		var other = await _pcs.SaveOnAAsync("https://shop.example.test");
		await _pcs.TurnOnAAsync();
		await _pcs.JoinBAsync();
		var backup = _pcs.Server.Backup();
		var id = await _pcs.SaveOnAAsync();
		await A.SyncAsync();
		_pcs.Server.Restore(backup);
		for (var i = 1; i <= writesOnB; i++)
		{
			B.Vault.UpdateSummary(other, $"Shop {i}", "", "ada");
			await B.SyncAsync();
		}
		return id;
	}

	[Fact]
	public async Task ALoginThatWasListedAndThatANewerManifestDropsWhileLiveHereIsHeldBack()
	{
		var id = await ListedLoginLostInARestoreAsync(writesOnB: 2);

		// B's second manifest is past A's floor, and it doesn't list X, which no tombstone came for (Ruling 58).
		var withheld = await Assert.ThrowsAsync<ServerWithheldItemException>(A.SyncAsync);

		Assert.Equal(id, withheld.ItemId);
		Assert.Equal("first-password", await A.Vault.PasswordAsync(id));
	}

	[Fact]
	public async Task AManifestAtThisPcsFloorWithOtherBytesIsARollback()
	{
		await ListedLoginLostInARestoreAsync(writesOnB: 1);

		// B's manifest has the version A wrote, with other entries: a forked manifest history (O2, Ruling 58).
		await Assert.ThrowsAsync<ServerRolledBackException>(A.SyncAsync);

		Assert.Equal("server-rolled-back", A.Engine.Status.Error);
	}

	[Fact]
	public async Task AnUploadThatCouldNotRunForASignedOutPcKeepsTheRollbackError()
	{
		await _pcs.SaveOnAAsync();
		await _pcs.TurnOnAAsync();
		var backup = _pcs.Server.Backup();
		await _pcs.SaveOnAAsync("https://shop.example.test");
		await A.SyncAsync();
		_pcs.Server.Restore(backup);
		await Assert.ThrowsAsync<ServerRolledBackException>(A.SyncAsync);
		A.Vault.ForgetSyncAccount();

		await Assert.ThrowsAsync<NotSignedInToSyncException>(() => A.Engine.UploadLocalCopyAsync(Ct));

		// Nothing was uploaded, so Settings still says why sync stopped (M7).
		Assert.Equal("server-rolled-back", A.Engine.Status.Error);
	}

	[Fact]
	public async Task UploadThisPcsCopyIsRefusedUnlessTheLastSyncStoppedOnARollbackOrAHeldBackLogin()
	{
		await _pcs.SaveOnAAsync();
		await _pcs.TurnOnAAsync();
		await A.SyncAsync();

		await Assert.ThrowsAsync<SyncUploadNotOfferedException>(() => A.Engine.UploadLocalCopyAsync(Ct));

		Assert.Null(A.Engine.Status.Error);
	}

	/// <summary>
	/// The server is restored from a backup after B changed a login's password, so it loses B's change; A, which never saw it, then
	/// saves another login and changes the same password later, as <paramref name="changesOnA"/> changes.
	/// </summary>
	private async Task<string> PasswordLostInARestoreAsync(int changesOnA)
	{
		var id = await _pcs.SaveOnAAsync();
		await _pcs.TurnOnAAsync();
		await _pcs.JoinBAsync();
		var backup = _pcs.Server.Backup();
		await B.Vault.UpdatePasswordAsync(id, "changed-on-b");
		await B.SyncAsync();
		_pcs.Server.Restore(backup);
		// A second login first, so A's change of this one lands past B's floor and B's next pull brings it.
		await _pcs.SaveOnAAsync("https://shop.example.test");
		await A.SyncAsync();
		A.Clock.Advance(TimeSpan.FromMinutes(1));
		for (var i = 1; i <= changesOnA; i++) await A.Vault.UpdatePasswordAsync(id, $"changed-on-a-{i}");
		await A.SyncAsync();
		return id;
	}

	private static async Task AssertPasswordsAsync(SyncPc pc, string id, string password, params string[] old)
	{
		Assert.Equal(password, await pc.Vault.PasswordAsync(id));
		var held = new List<string>();
		for (var i = 0; i < pc.Vault.History(id).Count; i++) held.Add(await pc.Vault.HistoryPasswordAsync(id, i));
		foreach (var each in old) Assert.Contains(each, held);
	}

	[Fact]
	public async Task UploadThisPcsCopyMergesSoNeitherPcsPasswordIsLost()
	{
		var id = await PasswordLostInARestoreAsync(changesOnA: 1);
		// A's copy has the version B's has, with other bytes (M3): the server lost the copy it took from B.
		await Assert.ThrowsAsync<ServerRolledBackException>(B.SyncAsync);

		await B.Engine.UploadLocalCopyAsync(Ct);
		await A.SyncAsync();

		// A changed it later, so A's password wins; B's moves into the history rather than being written over (Ruling 56).
		await AssertPasswordsAsync(A, id, "changed-on-a-1", "changed-on-b", "first-password");
		await AssertPasswordsAsync(B, id, "changed-on-a-1", "changed-on-b", "first-password");
	}

	[Fact]
	public async Task ANewerCopyWrittenWithoutThisPcsPasswordIsMergedNotTaken()
	{
		var id = await PasswordLostInARestoreAsync(changesOnA: 2);

		// A's copy is past B's version but doesn't hold B's password anywhere: it was written over a copy the server lost.
		await B.SyncAsync();
		await A.SyncAsync();

		await AssertPasswordsAsync(A, id, "changed-on-a-2", "changed-on-b", "changed-on-a-1", "first-password");
		await AssertPasswordsAsync(B, id, "changed-on-a-2", "changed-on-b", "changed-on-a-1", "first-password");
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

	/// <summary>The page or push answer with <paramref name="item"/> added to its <paramref name="list"/> ("items" or "conflicts").</summary>
	private static string AddItem(string body, string list, SyncItem item)
	{
		var page = JsonNode.Parse(body)!;
		page[list]!.AsArray().Add(JsonSerializer.SerializeToNode(item, SyncJson.Options));
		return page.ToJsonString();
	}

	private static string SetMore(string body)
	{
		var page = JsonNode.Parse(body)!;
		page["items"] = new JsonArray();
		page["more"] = true;
		return page.ToJsonString();
	}

	private static string FlagTombstone(string body, string id)
	{
		var page = JsonNode.Parse(body)!;
		foreach (var item in page["items"]!.AsArray().Where(i => i!["itemId"]!.GetValue<string>() == id)) item!["tombstone"] = true;
		return page.ToJsonString();
	}

	/// <summary>The page with <paramref name="id"/>'s password and its newest old password swapped, each ciphertext kept as it is.</summary>
	private static string SwapPasswordWithNewestOldOne(string body, string id)
	{
		var page = JsonNode.Parse(body)!;
		foreach (var item in page["items"]!.AsArray().Where(i => i!["itemId"]!.GetValue<string>() == id))
		{
			var password = Convert.FromBase64String(item!["password"]!.GetValue<string>());
			var history = Split(Convert.FromBase64String(item["history"]!.GetValue<string>()));
			(password, history[0]) = (history[0], password);
			item["password"] = Convert.ToBase64String(password);
			item["history"] = Convert.ToBase64String(SyncWire.JoinHistory(history));
		}
		return page.ToJsonString();

		static List<byte[]> Split(byte[] joined)
		{
			var parts = new List<byte[]>();
			for (var at = 0; at < joined.Length;)
			{
				var length = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(joined.AsSpan(at));
				parts.Add(joined[(at + 4)..(at + 4 + length)]);
				at += 4 + length;
			}
			return parts;
		}
	}

	private static string RemoveItem(string body, string id)
	{
		var page = JsonNode.Parse(body)!;
		var items = page["items"]!.AsArray();
		foreach (var item in items.Where(i => i!["itemId"]!.GetValue<string>() == id).ToList()) items.Remove(item);
		return page.ToJsonString();
	}
}
