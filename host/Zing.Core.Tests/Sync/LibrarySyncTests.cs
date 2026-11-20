using System.Net;
using System.Text.Json.Nodes;
using Zing.Core.Sync;
using Zing.Core.Vault;

namespace Zing.Core.Tests.Sync;

/// <summary>Library and settings sync between two PCs through the real sync server, in process.</summary>
public sealed class LibrarySyncTests : IAsyncLifetime
{
	private readonly TwoPcs _pcs = new();
	private Side _a = null!;
	private Side _b = null!;

	private static CancellationToken Ct => TestContext.Current.CancellationToken;

	/// <summary>One PC's library, settings and their sync, beside its vault.</summary>
	private sealed class Side(SyncPc pc)
	{
		public SyncPc Pc { get; } = pc;
		public LibraryStore Library { get; } = new(Zing.Core.Library.EmptyLibrary(1), _ => { }, pc.Clock.Now, () => Guid.NewGuid().ToString("N"));
		public SettingsStore Settings { get; } = new();
		public LibrarySync LibrarySync { get; private set; } = null!;
		public SettingsSync SettingsSync { get; private set; } = null!;

		public Side Start()
		{
			LibrarySync = new LibrarySync(Pc.Vault, Library, Settings, Pc.Clock);
			SettingsSync = new SettingsSync(Pc.Vault, Settings, Pc.Clock);
			return this;
		}

		private string User => Pc.Vault.SyncAccount()!.UserId;

		public Task<bool> SyncLibraryAsync() => LibrarySync.SyncAsync(Pc.Client, User, Ct);

		public Task SyncSettingsAsync() => SettingsSync.SyncAsync(Pc.Client, User, Ct);

		public BookmarkNode Node(string id) => Zing.Core.Library.Find(id, Library.Snapshot.Bookmarks) ?? throw new InvalidOperationException($"No node {id}");

		public IReadOnlyList<BookmarkNode> Children(string id) => Library.ChildrenOf(id);
	}

	public async ValueTask InitializeAsync()
	{
		await _pcs.SaveOnAAsync();
		await _pcs.TurnOnAAsync();
		await _pcs.JoinBAsync();
		_a = new Side(_pcs.A).Start();
		_b = new Side(_pcs.B).Start();
	}

	public ValueTask DisposeAsync()
	{
		_pcs.Dispose();
		return ValueTask.CompletedTask;
	}

	private static async Task SyncAll(params Side[] order)
	{
		foreach (var side in order) await side.SyncLibraryAsync();
	}

	[Fact]
	public async Task A_bookmark_made_on_one_pc_reaches_the_other_and_an_edit_comes_back()
	{
		var folder = _a.Library.Apply(new NewFolderAction([]))!;
		var link = _a.Library.AddLink(folder, 0, "Docs", "https://docs.test/");
		_a.Library.AddReading("Later", "https://later.test/");

		await SyncAll(_a, _b);
		_b.Library.Apply(new RenameAction(link, "Docs (B)"));
		await SyncAll(_b, _a);

		Assert.Equal("Docs (B)", _a.Node(link).Title);
		Assert.Equal([link], _b.Children(folder).Select(n => n.Id));
		Assert.Equal(["https://later.test/"], _b.Library.Snapshot.ReadingList.Select(i => i.Url));
		Assert.Equal(LibraryTree.Flatten(_a.Library.Snapshot), LibraryTree.Flatten(_b.Library.Snapshot), new SameEntries());
	}

	[Fact]
	public async Task A_rename_and_a_move_made_on_two_pcs_at_once_both_survive_on_both()
	{
		var folder = _a.Library.Apply(new NewFolderAction([]))!;
		_a.Library.AddFavourite("Mail", "https://mail.test/");
		await SyncAll(_a, _b);
		var link = Library.Favourites(_a.Library.Snapshot).Single().Id;

		_a.Library.Apply(new RenameAction(link, "Inbox"));
		_b.Library.Apply(new MoveIntoAction(link, folder, 0));
		await SyncAll(_a, _b, _a);

		foreach (var side in new[] { _a, _b })
		{
			Assert.Equal("Inbox", side.Node(link).Title);
			Assert.Equal([link], side.Children(folder).Select(n => n.Id));
			Assert.Empty(Library.Favourites(side.Library.Snapshot));
		}
	}

	[Fact]
	public async Task A_delete_on_one_pc_and_an_edit_on_the_other_keep_the_edited_node_on_both()
	{
		_a.Library.AddFavourite("Keep", "https://keep.test/");
		_a.Library.AddFavourite("Gone", "https://gone.test/");
		await SyncAll(_a, _b);
		var keep = Library.Favourites(_a.Library.Snapshot)[0].Id;
		var gone = Library.Favourites(_a.Library.Snapshot)[1].Id;

		_a.Library.Apply(new RemoveAction([keep, gone]));
		_b.Library.Apply(new RenameAction(keep, "Kept"));
		await SyncAll(_b, _a, _b);

		foreach (var side in new[] { _a, _b })
			Assert.Equal(["Kept"], Library.Favourites(side.Library.Snapshot).Select(n => n.Title));
	}

	[Fact]
	public async Task A_folder_deleted_on_one_pc_while_the_other_moves_a_bookmark_into_it()
	{
		var folder = _a.Library.Apply(new NewFolderAction([]))!;
		_a.Library.AddFavourite("Mail", "https://mail.test/");
		await SyncAll(_a, _b);
		var link = Library.Favourites(_a.Library.Snapshot).Single().Id;

		_a.Library.Apply(new RemoveAction([folder]));
		_b.Library.Apply(new MoveIntoAction(link, folder, 0));
		await SyncAll(_a, _b, _a);

		foreach (var side in new[] { _a, _b })
			Assert.Equal([link], side.Children(folder).Select(n => n.Id));
		Assert.Equal(LibraryTree.Flatten(_a.Library.Snapshot), LibraryTree.Flatten(_b.Library.Snapshot), new SameEntries());
	}

	[Fact]
	public async Task A_v1_client_pulling_the_vault_never_sees_a_library_item()
	{
		for (var i = 0; i < 40; i++) _a.Library.AddFavourite($"Site {i}", $"https://site{i}.test/");
		await SyncAll(_a, _b);

		var vault = await _pcs.ServerItemsAsync();
		await _pcs.B.SyncAsync();

		Assert.NotEmpty(vault.Items);
		Assert.All(vault.Items, item => Assert.DoesNotContain("library", item.ItemId, StringComparison.Ordinal));
		Assert.Equal(_pcs.A.Vault.Summaries().Select(s => s.Id), _pcs.B.Vault.Summaries().Select(s => s.Id));
		Assert.Equal(40, Library.Favourites(_b.Library.Snapshot).Count);
	}

	[Fact]
	public async Task A_server_without_library_routes_is_sent_nothing_of_the_library()
	{
		_pcs.Server.Wire.AnswerWith = request => request.RequestUri!.AbsolutePath.StartsWith("/library", StringComparison.Ordinal) || request.RequestUri.AbsolutePath == "/settings" ? HttpStatusCode.NotFound : null;
		_a.Library.AddFavourite("Mail", "https://mail.test/");
		_a.Settings.Set("theme", "dark");

		await Assert.ThrowsAsync<SyncFeatureMissingException>(_a.SyncLibraryAsync);
		await Assert.ThrowsAsync<SyncFeatureMissingException>(_a.SyncSettingsAsync);

		Assert.DoesNotContain(_pcs.Server.Wire.Log, line => line is "POST /library/changes" or "PUT /library/manifest" or "PUT /settings");
	}

	[Fact]
	public async Task The_engine_syncs_the_library_after_the_logins_and_warns_when_the_server_is_too_old()
	{
		using var engine = new SyncEngine(_pcs.A.Vault, _pcs.A.Client, new TestScheduler(), _pcs.A.Clock, new LibrarySync(_pcs.A.Vault, _a.Library, _a.Settings, _pcs.A.Clock), new SettingsSync(_pcs.A.Vault, _a.Settings, _pcs.A.Clock));
		_a.Library.AddFavourite("Mail", "https://mail.test/");

		await engine.SyncAsync(Ct);
		await _b.SyncLibraryAsync();
		Assert.Single(Library.Favourites(_b.Library.Snapshot));
		Assert.Null(engine.Status.Warning);

		_pcs.Server.Wire.AnswerWith = request => request.RequestUri!.AbsolutePath.StartsWith("/library", StringComparison.Ordinal) ? HttpStatusCode.NotFound : null;
		await engine.SyncAsync(Ct);
		Assert.Null(engine.Status.Error);
		Assert.Contains("stay on this PC", engine.Status.Warning, StringComparison.Ordinal);
	}

	[Fact]
	public async Task The_engine_leaves_the_library_alone_when_this_pc_turned_it_off()
	{
		_a.Settings.Set("syncLibrary", false);
		using var engine = new SyncEngine(_pcs.A.Vault, _pcs.A.Client, new TestScheduler(), _pcs.A.Clock, new LibrarySync(_pcs.A.Vault, _a.Library, _a.Settings, _pcs.A.Clock));
		_a.Library.AddFavourite("Mail", "https://mail.test/");

		await engine.SyncAsync(Ct);

		Assert.DoesNotContain(_pcs.Server.Wire.Log, line => line.Contains("/library", StringComparison.Ordinal));
	}

	[Fact]
	public async Task A_server_serving_an_older_library_stops_library_sync()
	{
		_a.Library.AddFavourite("One", "https://one.test/");
		await _a.SyncLibraryAsync();
		var backup = _pcs.Server.Backup();
		_a.Library.AddFavourite("Two", "https://two.test/");
		await _a.SyncLibraryAsync();

		_pcs.Server.Restore(backup);

		await Assert.ThrowsAsync<LibrarySyncStoppedException>(_a.SyncLibraryAsync);
		Assert.Equal(2, Library.Favourites(_a.Library.Snapshot).Count);
	}

	[Fact]
	public async Task Turning_sync_off_starts_library_sync_over()
	{
		_a.Library.AddFavourite("One", "https://one.test/");
		await _a.SyncLibraryAsync();
		Assert.True(File.Exists(_pcs.A.Vault.CollectionStateFile(VaultStore.LibrarySyncFileName)));

		_pcs.A.Vault.ForgetSyncAccount();
		_pcs.A.Vault.TurnOffSync();

		Assert.False(File.Exists(_pcs.A.Vault.CollectionStateFile(VaultStore.LibrarySyncFileName)));
	}

	[Fact]
	public async Task A_setting_changed_on_one_pc_reaches_the_other_and_local_settings_stay()
	{
		_a.Settings.Set("theme", "dark");
		_a.Settings.Set("restoreTabs", false);
		_a.Settings.Set("verticalTabWidth", 300);

		await _a.SyncSettingsAsync();
		await _b.SyncSettingsAsync();

		Assert.Equal("dark", _b.Settings.Get("theme")!.GetValue<string>());
		Assert.True(_b.Settings.Get("restoreTabs")!.GetValue<bool>());
		Assert.NotEqual(300, _b.Settings.Get("verticalTabWidth")!.GetValue<double>());
	}

	[Fact]
	public async Task Settings_changed_on_both_pcs_take_the_last_change()
	{
		await _a.SyncSettingsAsync();
		await _b.SyncSettingsAsync();

		_b.Settings.Set("theme", "light");
		_pcs.A.Clock.Advance(TimeSpan.FromMinutes(1));
		_a.Settings.Set("theme", "dark");
		await _b.SyncSettingsAsync();
		await _a.SyncSettingsAsync();
		await _b.SyncSettingsAsync();

		Assert.Equal("dark", _a.Settings.Get("theme")!.GetValue<string>());
		Assert.Equal("dark", _b.Settings.Get("theme")!.GetValue<string>());
	}

	[Fact]
	public async Task A_pc_that_changed_nothing_takes_the_servers_settings_on_its_first_sync()
	{
		_a.Settings.Set("searchEngine", "duckduckgo");
		await _a.SyncSettingsAsync();

		await _b.SyncSettingsAsync();

		Assert.Equal("duckduckgo", _b.Settings.Get("searchEngine")!.GetValue<string>());
		Assert.Equal(JsonValue.Create("duckduckgo").ToJsonString(), _a.Settings.Get("searchEngine")!.ToJsonString());
	}

	[Fact]
	public void Settings_kept_on_each_pc_are_all_real_settings()
	{
		Assert.All(SettingsSync.LocalOnly, key => Assert.True(SettingsSchema.IsSettingKey(key), key));
	}

	private sealed class SameEntries : IEqualityComparer<Dictionary<string, LibraryEntry>>
	{
		public bool Equals(Dictionary<string, LibraryEntry>? a, Dictionary<string, LibraryEntry>? b) => a is not null && b is not null && LibraryTree.Same(a, b);

		public int GetHashCode(Dictionary<string, LibraryEntry> obj) => obj.Count;
	}
}
