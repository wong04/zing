using System.Text.Json;
using Zing.Core.Import;
using Zing.Core.Net;

namespace Zing.Core.Tests.Import;

/// <summary>import:sources, import:bookmarks and import:bookmarks-file over a fake LocalAppData root.</summary>
public sealed class ImportApiTests : IDisposable
{
	private readonly string _root = Directory.CreateTempSubdirectory("zing-importapi-").FullName;
	private readonly Bridge _bridge = new();
	private readonly FakeWindowHost _host = new();
	private readonly LibraryStore _library = new(Library.EmptyLibrary(0), _ => { }, Deterministic.Clock(), Deterministic.Ids("id-"));
	private readonly ChromeApi _api;

	public ImportApiTests()
	{
		_api = new ChromeApi(_bridge, new AppServices(new SettingsStore(), _library, Address.FromDefaults(), new TickingClock(), Accounts.Guest(), new RelayConsent(), LocalAppData: () => _root, AppData: () => _root), _host, "test");
	}

	public void Dispose()
	{
		_api.Dispose();
		Directory.Delete(_root, recursive: true);
	}

	private static string Fixture(string name) => File.ReadAllText(Path.Combine(Vectors.RepoRoot(), "src/shared/import/fixtures", name));

	private void WriteChrome(string profile, string name)
	{
		var userData = Path.Combine(_root, "Google", "Chrome", "User Data");
		Directory.CreateDirectory(Path.Combine(userData, profile));
		File.WriteAllText(Path.Combine(userData, "Local State"), JsonSerializer.Serialize(new { profile = new { info_cache = new Dictionary<string, object> { [profile] = new { name } } } }));
		File.WriteAllText(Path.Combine(userData, profile, "Bookmarks"), Fixture("chromium-bookmarks.json"));
	}

	private void WriteArc()
	{
		var dir = Path.Combine(_root, "Packages", "TheBrowserCompany.Arc_abc", "LocalCache", "Local", "Arc");
		Directory.CreateDirectory(dir);
		File.WriteAllText(Path.Combine(dir, "StorableSidebar.json"), Fixture("arc-sidebar.json"));
	}

	private static async Task<JsonElement> Invoke(Bridge bridge, string channel, params object?[] args)
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
	public async Task SourcesListsRowsWithoutPaths()
	{
		WriteChrome("Default", "Personal");
		WriteArc();

		var result = await Invoke(_bridge, "import:sources");

		var rows = Value(result);
		Assert.Equal(["chrome:Default", "arc"], rows.EnumerateArray().Select(r => r.GetProperty("id").GetString()));
		Assert.Equal([true, false], rows.EnumerateArray().Select(r => r.GetProperty("passwords").GetBoolean()));
		Assert.Equal("Chrome (Personal)", rows[0].GetProperty("display").GetString());
		Assert.DoesNotContain("Path", result.GetRawText(), StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain(_root, result.GetRawText());
	}

	private void WriteVivaldi()
	{
		var userData = Path.Combine(_root, "Vivaldi", "User Data");
		Directory.CreateDirectory(Path.Combine(userData, "Default"));
		File.WriteAllText(Path.Combine(userData, "Local State"), JsonSerializer.Serialize(new { profile = new { info_cache = new Dictionary<string, object> { ["Default"] = new { name = "Home" } } } }));
		File.WriteAllText(Path.Combine(userData, "Default", "Bookmarks"), Fixture("chromium-bookmarks.json"));
	}

	private void WriteOpera()
	{
		var dir = Path.Combine(_root, "Opera Software", "Opera Stable");
		Directory.CreateDirectory(dir);
		File.WriteAllText(Path.Combine(dir, "Bookmarks"), Fixture("chromium-bookmarks.json"));
	}

	[Fact]
	public async Task VivaldiAndOperaRowsOfferBookmarksOnly()
	{
		WriteVivaldi();
		WriteOpera();

		var rows = Value(await Invoke(_bridge, "import:sources"));

		Assert.Equal(["vivaldi:Default", "opera:stable"], rows.EnumerateArray().Select(r => r.GetProperty("id").GetString()));
		Assert.Equal(["vivaldi", "opera"], rows.EnumerateArray().Select(r => r.GetProperty("browser").GetString()));
		Assert.Equal([false, false], rows.EnumerateArray().Select(r => r.GetProperty("passwords").GetBoolean()));
	}

	[Fact]
	public async Task ImportingOperaMarksItsBookmarksWithTheSourceId()
	{
		WriteOpera();

		var outcome = Value(await Invoke(_bridge, "import:bookmarks", "opera:stable"));

		Assert.Equal("Imported from Opera", outcome.GetProperty("folderTitle").GetString());
		Assert.Contains("opera:stable", Marks(_library.Snapshot.Bookmarks));
		var again = Value(await Invoke(_bridge, "import:bookmarks", "opera:stable"));
		Assert.Equal(0, again.GetProperty("addedToBar").GetInt32() + again.GetProperty("addedToFolder").GetInt32());
	}

	[Fact]
	public async Task ImportingASourceMergesIntoTheLibrary()
	{
		WriteChrome("Default", "Personal");

		var outcome = Value(await Invoke(_bridge, "import:bookmarks", "chrome:Default"));

		Assert.Equal("Imported from Chrome (Personal)", outcome.GetProperty("folderTitle").GetString());
		Assert.True(outcome.GetProperty("addedToBar").GetInt32() + outcome.GetProperty("addedToFolder").GetInt32() > 0);
		Assert.Equal(JsonValueKind.Array, outcome.GetProperty("skipped").ValueKind);
		Assert.NotEqual(Library.EmptyLibrary(0), _library.Snapshot);
	}

	[Fact]
	public async Task ReimportingAfterTheProfileIsRenamedAddsNothing()
	{
		WriteChrome("Default", "Personal");
		Value(await Invoke(_bridge, "import:bookmarks", "chrome:Default"));
		var before = _library.Snapshot;
		WriteChrome("Default", "Work");

		var outcome = Value(await Invoke(_bridge, "import:bookmarks", "chrome:Default"));

		Assert.Equal(0, outcome.GetProperty("addedToBar").GetInt32() + outcome.GetProperty("addedToFolder").GetInt32());
		Assert.Same(before, _library.Snapshot);
		Assert.Single(_library.Snapshot.Bookmarks, n => n.Title == "Imported from Chrome (Personal)");
	}

	[Fact]
	public async Task AnImportMarkedWithTheOldDisplayNameIsAdoptedNotDoubled()
	{
		WriteChrome("Default", "Personal");
		Value(await Invoke(_bridge, "import:bookmarks", "chrome:Default"));
		// What the library held after an import by a version that marked with the display name.
		var legacy = new LibraryStore(Relabel(_library.Snapshot, "chrome:Default", "Chrome (Personal)"), _ => { }, Deterministic.Clock(), Deterministic.Ids("old-"));
		var count = Library.Urls(new BookmarkFolder("all", "", legacy.Snapshot.Bookmarks, 0)).Count;
		var bridge = new Bridge();
		using var api = new ChromeApi(bridge, new AppServices(new SettingsStore(), legacy, Address.FromDefaults(), new TickingClock(), Accounts.Guest(), new RelayConsent(), LocalAppData: () => _root, AppData: () => _root), new FakeWindowHost(), "test");

		var outcome = Value(await Invoke(bridge, "import:bookmarks", "chrome:Default"));

		Assert.Equal(0, outcome.GetProperty("addedToBar").GetInt32() + outcome.GetProperty("addedToFolder").GetInt32());
		Assert.Equal(count, Library.Urls(new BookmarkFolder("all", "", legacy.Snapshot.Bookmarks, 0)).Count);
		Assert.DoesNotContain(Marks(legacy.Snapshot.Bookmarks), m => m == "Chrome (Personal)");
	}

	private static BrowserLibrary Relabel(BrowserLibrary library, string from, string to)
	{
		BookmarkNode Walk(BookmarkNode node) => node switch
		{
			BookmarkLink link => link.ImportedFrom == from ? link with { ImportedFrom = to } : link,
			BookmarkFolder folder => folder with { Children = folder.Children.Select(Walk).ToList(), ImportedFrom = folder.ImportedFrom == from ? to : folder.ImportedFrom },
			_ => node,
		};
		return library with { Bookmarks = library.Bookmarks.Select(Walk).ToList() };
	}

	private static IEnumerable<string?> Marks(IEnumerable<BookmarkNode> nodes) =>
		nodes.SelectMany(n => n is BookmarkFolder f ? Marks(f.Children).Prepend(f.ImportedFrom) : [Library.ImportedFromOf(n)]);

	[Fact]
	public async Task ImportingAPickedFileUsesItsNameAsTheSource()
	{
		var path = Path.Combine(_root, "my-export.json");
		File.WriteAllText(path, Fixture("chromium-bookmarks.json"));
		_host.PickedPaths = [path];
		var token = Value(await Invoke(_bridge, "files:pick", (object)new[] { "bookmarks" }))[0].GetProperty("token").GetString();

		var outcome = Value(await Invoke(_bridge, "import:bookmarks-file", token));

		Assert.Equal(["html", "htm", "json"], _host.PickedExtensions);
		Assert.Contains("my-export.json", outcome.GetProperty("folderTitle").GetString());
		Assert.DoesNotContain(_root, outcome.GetRawText());
		Assert.Contains("file:my-export.json", Marks(_library.Snapshot.Bookmarks));
	}

	[Fact]
	public async Task UnknownSourceThrows()
	{
		WriteChrome("Default", "Personal");

		var result = await Invoke(_bridge, "import:bookmarks", "chrome:Nope");

		Assert.False(result.GetProperty("ok").GetBoolean());
		Assert.Equal("UnknownImportSourceException", result.GetProperty("error").GetProperty("name").GetString());
		Assert.Contains("chrome:Nope", result.GetProperty("error").GetProperty("message").GetString());
	}

	[Fact]
	public async Task AnUnissuedTokenThrows()
	{
		var result = await Invoke(_bridge, "import:bookmarks-file", "never-issued");

		Assert.False(result.GetProperty("ok").GetBoolean());
		Assert.Equal("UnknownFileGrantException", result.GetProperty("error").GetProperty("name").GetString());
	}

	[Fact]
	public async Task APickedFileThatIsNotBookmarksThrowsWithoutItsDirectory()
	{
		var path = Path.Combine(_root, "notes.txt");
		File.WriteAllText(path, "just some text");
		_host.PickedPaths = [path];
		var token = Value(await Invoke(_bridge, "files:pick", (object)new[] { "bookmarks" }))[0].GetProperty("token").GetString();

		var result = await Invoke(_bridge, "import:bookmarks-file", token);

		Assert.Equal("UnknownBookmarksFileException", result.GetProperty("error").GetProperty("name").GetString());
		var message = result.GetProperty("error").GetProperty("message").GetString()!;
		Assert.Contains("notes.txt", message);
		Assert.DoesNotContain(_root, message);
	}
}
