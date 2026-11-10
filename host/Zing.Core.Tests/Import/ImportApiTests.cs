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
		_api = new ChromeApi(_bridge, new AppServices(new SettingsStore(), _library, Address.FromDefaults(), new TickingClock(), Accounts.Guest(), new RelayConsent(), LocalAppData: () => _root), _host, "test");
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
