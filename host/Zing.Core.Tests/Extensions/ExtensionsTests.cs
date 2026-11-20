using System.Text.Json;
using Zing.Core.Extensions;

namespace Zing.Core.Tests.Extensions;

/// <summary>An unpacked extension on disk, in a temp folder of its own.</summary>
internal static class Unpacked
{
	public static string Make(string root, string name = "Test Extension", string version = "1.0", string? popup = "popup.html", int manifestVersion = 3, string? folder = null)
	{
		var dir = Path.Combine(root, folder ?? Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(dir);
		var manifest = new Dictionary<string, object> { ["manifest_version"] = manifestVersion, ["name"] = name, ["version"] = version };
		if (popup is not null) manifest["action"] = new { default_popup = popup };
		File.WriteAllText(Path.Combine(dir, "manifest.json"), JsonSerializer.Serialize(manifest));
		File.WriteAllText(Path.Combine(dir, "content.js"), "// content");
		Directory.CreateDirectory(Path.Combine(dir, "lib"));
		File.WriteAllText(Path.Combine(dir, "lib", "deep.js"), "// deep");
		return dir;
	}
}

/// <summary>The engine's side, as WebView2's profile keeps it: ids by folder, as an unpacked extension's id is its path's.</summary>
internal sealed class FakeExtensionEngine : IExtensionEngine
{
	public Dictionary<string, (string Folder, bool Enabled)> Loaded { get; } = new(StringComparer.Ordinal);
	public List<string> Calls { get; } = [];
	public Exception? FailList { get; set; }

	public static string IdOf(string folder) => "id-" + Path.GetFileName(folder);

	public Task<IReadOnlyList<EngineExtension>> ListAsync()
	{
		Calls.Add("list");
		if (FailList is { } error) throw error;
		return Task.FromResult<IReadOnlyList<EngineExtension>>([.. Loaded.Select(e => new EngineExtension(e.Key, Path.GetFileName(e.Value.Folder), e.Value.Enabled))]);
	}

	public Task<EngineExtension> AddAsync(string folder)
	{
		Calls.Add("add " + Path.GetFileName(folder));
		if (!File.Exists(Path.Combine(folder, "manifest.json"))) throw new FileNotFoundException("no manifest", folder);
		var id = IdOf(folder);
		Loaded[id] = (folder, true);
		return Task.FromResult(new EngineExtension(id, Path.GetFileName(folder), true));
	}

	public Task SetEnabledAsync(string id, bool enabled)
	{
		Calls.Add($"enable {id} {enabled}");
		Loaded[id] = (Loaded[id].Folder, enabled);
		return Task.CompletedTask;
	}

	public Task RemoveAsync(string id)
	{
		Calls.Add("remove " + id);
		if (!Loaded.Remove(id)) throw new UnknownExtensionException(id);
		return Task.CompletedTask;
	}
}

public sealed class ExtensionManifestTests : IDisposable
{
	private readonly string _root = Directory.CreateTempSubdirectory("zing-ext-manifest-").FullName;

	public void Dispose() => Directory.Delete(_root, recursive: true);

	[Fact]
	public void Reads_name_version_and_the_action_popup()
	{
		var manifest = ExtensionManifest.Read(Unpacked.Make(_root, "Probe", "2.3.1", "/ui/popup.html"));

		Assert.Equal(new ExtensionManifest("Probe", "2.3.1", "ui/popup.html"), manifest);
	}

	[Fact]
	public void An_extension_without_an_action_popup_has_none()
	{
		Assert.Null(ExtensionManifest.Read(Unpacked.Make(_root, popup: null)).Popup);
	}

	[Fact]
	public void A_name_from_the_default_locale_is_looked_up_ignoring_case_as_chromium_does()
	{
		var dir = Path.Combine(_root, "localized");
		Directory.CreateDirectory(Path.Combine(dir, "_locales", "en"));
		File.WriteAllText(Path.Combine(dir, "manifest.json"), """{ "manifest_version": 3, "name": "__MSG_extName__", "version": "1", "default_locale": "en" }""");
		File.WriteAllText(Path.Combine(dir, "_locales", "en", "messages.json"), """{ "EXTNAME": { "message": "uBlock Origin Lite" } }""");

		Assert.Equal("uBlock Origin Lite", ExtensionManifest.Read(dir).Name);
	}

	[Fact]
	public void A_folder_without_a_manifest_is_refused_with_why()
	{
		var dir = Directory.CreateDirectory(Path.Combine(_root, "empty")).FullName;

		var error = Assert.Throws<InvalidExtensionException>(() => ExtensionManifest.Read(dir));
		Assert.Contains("no manifest.json", error.Message);
	}

	[Fact]
	public void A_manifest_v2_extension_is_refused()
	{
		var error = Assert.Throws<InvalidExtensionException>(() => ExtensionManifest.Read(Unpacked.Make(_root, manifestVersion: 2)));
		Assert.Contains("Manifest V3", error.Message);
	}

	[Fact]
	public void A_manifest_that_isnt_json_is_refused()
	{
		var dir = Directory.CreateDirectory(Path.Combine(_root, "broken")).FullName;
		File.WriteAllText(Path.Combine(dir, "manifest.json"), "{ not json");

		Assert.Throws<InvalidExtensionException>(() => ExtensionManifest.Read(dir));
	}
}

public sealed class ExtensionStoreTests : IDisposable
{
	private readonly string _root = Directory.CreateTempSubdirectory("zing-ext-store-").FullName;
	private string Store => Path.Combine(_root, "Extensions");

	public void Dispose() => Directory.Delete(_root, recursive: true);

	[Fact]
	public void Install_copies_the_whole_folder_in_and_lists_it_after_a_reopen()
	{
		var source = Unpacked.Make(Path.Combine(_root, "src"), "My Ext");
		var store = new ExtensionStore(Store);

		var (item, replaced) = store.Install(source);

		Assert.False(replaced);
		Assert.StartsWith("my-ext-", item.Key);
		Assert.True(File.Exists(Path.Combine(store.FolderOf(item.Key), "lib", "deep.js")));
		var reopened = new ExtensionStore(Store);
		Assert.Equal([new InstalledExtension(item.Key, source, true, null)], reopened.Items);
	}

	[Fact]
	public void The_copy_keeps_working_after_the_original_is_deleted()
	{
		var source = Unpacked.Make(Path.Combine(_root, "src"));
		var store = new ExtensionStore(Store);
		var (item, _) = store.Install(source);

		Directory.Delete(source, recursive: true);

		Assert.Equal("Test Extension", store.ManifestOf(item.Key).Name);
	}

	[Fact]
	public void Loading_the_same_folder_again_replaces_the_copy_under_the_same_key()
	{
		var source = Unpacked.Make(Path.Combine(_root, "src"), version: "1.0", folder: "ext");
		var store = new ExtensionStore(Store);
		var (first, _) = store.Install(source);
		Unpacked.Make(Path.Combine(_root, "src"), version: "2.0", folder: "ext");
		File.Delete(Path.Combine(source, "content.js"));

		var (second, replaced) = store.Install(source);

		Assert.True(replaced);
		Assert.Equal(first.Key, second.Key);
		Assert.Single(store.Items);
		Assert.Equal("2.0", store.ManifestOf(first.Key).Version);
		Assert.False(File.Exists(Path.Combine(store.FolderOf(first.Key), "content.js")));
	}

	[Fact]
	public void A_folder_that_isnt_an_extension_leaves_nothing_behind()
	{
		var store = new ExtensionStore(Store);
		var notOne = Directory.CreateDirectory(Path.Combine(_root, "photos")).FullName;

		Assert.Throws<InvalidExtensionException>(() => store.Install(notOne));

		Assert.Empty(store.Items);
		Assert.Empty(Directory.GetDirectories(Store));
	}

	[Fact]
	public void A_link_inside_the_folder_is_refused_and_the_partial_copy_removed()
	{
		var source = Unpacked.Make(Path.Combine(_root, "src"));
		var target = Directory.CreateDirectory(Path.Combine(_root, "elsewhere")).FullName;
		try
		{
			Directory.CreateSymbolicLink(Path.Combine(source, "linked"), target);
		}
		catch (IOException)
		{
			// Symbolic links need Developer Mode or elevation; a junction needs neither but has no managed API.
			return;
		}
		catch (UnauthorizedAccessException)
		{
			return;
		}
		var store = new ExtensionStore(Store);

		var error = Assert.Throws<InvalidExtensionException>(() => store.Install(source));

		Assert.Contains("linked is a link", error.Message);
		Assert.Empty(store.Items);
		Assert.DoesNotContain(Directory.GetDirectories(Store), d => Path.GetFileName(d).StartsWith(".tmp-", StringComparison.Ordinal));
	}

	[Fact]
	public void Remove_deletes_the_copy_and_keeps_the_engine_id_to_forget_when_the_engine_wasnt_told()
	{
		var store = new ExtensionStore(Store);
		var (item, _) = store.Install(Unpacked.Make(Path.Combine(_root, "src")));
		store.SetId(item.Key, "abc");

		store.Remove(item.Key, engineForgot: false);

		Assert.Empty(store.Items);
		Assert.False(Directory.Exists(store.FolderOf(item.Key)));
		Assert.Equal(["abc"], new ExtensionStore(Store).Forget);
	}

	[Fact]
	public void A_copy_deleted_from_disk_drops_out_of_the_list_at_the_next_open()
	{
		var store = new ExtensionStore(Store);
		var (item, _) = store.Install(Unpacked.Make(Path.Combine(_root, "src")));
		Directory.Delete(store.FolderOf(item.Key), recursive: true);

		Assert.Empty(new ExtensionStore(Store).Items);
	}

	[Fact]
	public void An_unknown_key_says_so()
	{
		Assert.Throws<UnknownExtensionException>(() => new ExtensionStore(Store).SetEnabled("nope", false));
	}
}

public sealed class ExtensionServiceTests : IDisposable
{
	private readonly string _root = Directory.CreateTempSubdirectory("zing-ext-service-").FullName;
	private readonly FakeExtensionEngine _engine = new();
	private string Store => Path.Combine(_root, "Extensions");

	public void Dispose() => Directory.Delete(_root, recursive: true);

	private string Source(string name = "Test Extension", string? popup = "popup.html") => Unpacked.Make(Path.Combine(_root, "src"), name, popup: popup);

	[Fact]
	public async Task Not_running_installs_the_copy_but_never_asks_the_engine_and_says_it_isnt_running()
	{
		var service = new ExtensionService(new ExtensionStore(Store), running: false);

		var row = await service.InstallAsync(Source());

		Assert.Equal("Test Extension", row.Name);
		Assert.False(service.State().Running);
		Assert.Single(service.State().Items);
		await Assert.ThrowsAsync<ExtensionsNotRunningException>(() => service.AttachAsync(_engine));
		await Assert.ThrowsAsync<ExtensionsNotRunningException>(() => service.PopupUrlAsync(row.Key));
		Assert.Empty(_engine.Calls);
	}

	[Fact]
	public async Task Attach_loads_what_the_profile_lacks_and_matches_each_ones_on_off()
	{
		var store = new ExtensionStore(Store);
		var on = store.Install(Source("On")).Item;
		var off = store.Install(Source("Off")).Item;
		store.SetEnabled(off.Key, false);
		var service = new ExtensionService(store, running: true);

		await service.AttachAsync(_engine);

		Assert.Equal(["list", $"add {on.Key}", $"add {off.Key}", $"enable id-{off.Key} False"], _engine.Calls);
		Assert.Equal($"id-{on.Key}", store.Get(on.Key).Id);
		Assert.False(_engine.Loaded[$"id-{off.Key}"].Enabled);
	}

	[Fact]
	public async Task A_second_launch_with_the_profile_kept_adds_nothing()
	{
		var store = new ExtensionStore(Store);
		var item = store.Install(Source()).Item;
		await new ExtensionService(store, running: true).AttachAsync(_engine);
		_engine.Calls.Clear();

		await new ExtensionService(new ExtensionStore(Store), running: true).AttachAsync(_engine);

		Assert.Equal(["list"], _engine.Calls);
		Assert.True(_engine.Loaded.ContainsKey($"id-{item.Key}"));
	}

	[Fact]
	public async Task Every_page_waits_for_the_same_attach()
	{
		var service = new ExtensionService(new ExtensionStore(Store), running: true);

		var first = service.AttachAsync(_engine);
		var second = service.AttachAsync(_engine);

		Assert.Same(first, second);
		await first;
		Assert.Equal(["list"], _engine.Calls);
	}

	[Fact]
	public async Task An_attach_that_fails_fails_the_calls_waiting_on_the_engine_too()
	{
		var store = new ExtensionStore(Store);
		store.Install(Source());
		var service = new ExtensionService(store, running: true);
		_engine.FailList = new InvalidOperationException("engine gone");

		await Assert.ThrowsAsync<InvalidOperationException>(() => service.AttachAsync(_engine));

		await Assert.ThrowsAsync<InvalidOperationException>(() => service.InstallAsync(Source("Later")));
	}

	[Fact]
	public async Task Running_install_enable_and_remove_go_to_the_engine_and_the_store()
	{
		var service = new ExtensionService(new ExtensionStore(Store), running: true);
		await service.AttachAsync(_engine);

		var row = await service.InstallAsync(Source());
		var id = $"id-{row.Key}";
		Assert.True(_engine.Loaded[id].Enabled);

		await service.SetEnabledAsync(row.Key, false);
		Assert.False(_engine.Loaded[id].Enabled);
		Assert.False(service.State().Items.Single().Enabled);

		await service.RemoveAsync(row.Key);
		Assert.Empty(_engine.Loaded);
		Assert.Empty(service.State().Items);
		Assert.Empty(new ExtensionStore(Store).Forget);
	}

	[Fact]
	public async Task Loading_the_same_folder_again_unloads_the_old_copy_first()
	{
		var source = Source();
		var service = new ExtensionService(new ExtensionStore(Store), running: true);
		await service.AttachAsync(_engine);
		var row = await service.InstallAsync(source);
		_engine.Calls.Clear();

		await service.InstallAsync(source);

		Assert.Equal([$"remove id-{row.Key}", $"add {row.Key}"], _engine.Calls);
		Assert.Single(service.State().Items);
	}

	[Fact]
	public async Task One_removed_while_not_running_is_forgotten_by_the_engine_at_the_next_running_launch()
	{
		var store = new ExtensionStore(Store);
		var item = store.Install(Source()).Item;
		await new ExtensionService(store, running: true).AttachAsync(_engine);

		await new ExtensionService(new ExtensionStore(Store), running: false).RemoveAsync(item.Key);
		await new ExtensionService(new ExtensionStore(Store), running: true).AttachAsync(_engine);

		Assert.Empty(_engine.Loaded);
		Assert.Empty(new ExtensionStore(Store).Forget);
	}

	[Fact]
	public async Task The_popup_is_the_extensions_own_page_by_its_engine_id()
	{
		var service = new ExtensionService(new ExtensionStore(Store), running: true);
		await service.AttachAsync(_engine);
		var row = await service.InstallAsync(Source());

		Assert.Equal($"chrome-extension://id-{row.Key}/popup.html", await service.PopupUrlAsync(row.Key));
	}

	[Fact]
	public async Task One_without_a_popup_or_turned_off_has_none_to_open()
	{
		var service = new ExtensionService(new ExtensionStore(Store), running: true);
		await service.AttachAsync(_engine);
		var bare = await service.InstallAsync(Source("Bare", popup: null));
		var off = await service.InstallAsync(Source("Off"));
		await service.SetEnabledAsync(off.Key, false);

		await Assert.ThrowsAsync<NoExtensionPopupException>(() => service.PopupUrlAsync(bare.Key));
		await Assert.ThrowsAsync<InvalidOperationException>(() => service.PopupUrlAsync(off.Key));
	}

	[Fact]
	public void A_copy_whose_manifest_broke_is_listed_with_the_problem_rather_than_hidden()
	{
		var store = new ExtensionStore(Store);
		var item = store.Install(Source()).Item;
		File.WriteAllText(Path.Combine(store.FolderOf(item.Key), "manifest.json"), "{ broken");

		var row = new ExtensionService(store, running: false).State().Items.Single();

		Assert.NotNull(row.Problem);
		Assert.False(row.HasPopup);
	}
}
