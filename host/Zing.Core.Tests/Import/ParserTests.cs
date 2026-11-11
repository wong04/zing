using System.Text.Json;
using System.Text.Json.Nodes;
using Zing.Core.Import;
using Zing.Core.Vault;

namespace Zing.Core.Tests.Import;

/// <summary>The C# twins of src/shared/import/{chromiumBookmarks,netscapeBookmarks,arcSidebar}.ts, held to them by parsers.vectors.json and its fixtures.</summary>
public sealed class ParserTests : IDisposable
{
	private const string VectorsPath = "src/shared/import/parsers.vectors.json";

	private readonly string _dir = Directory.CreateTempSubdirectory("zing-importparsers-").FullName;

	public void Dispose() => Directory.Delete(_dir, recursive: true);

	public static TheoryData<string> ParseCases() => [.. Vectors.Load(VectorsPath).GetProperty("parse").EnumerateArray().Select(v => v.GetProperty("name").GetString()!)];

	public static TheoryData<string> RefusedCases() => [.. Vectors.Load(VectorsPath).GetProperty("refused").EnumerateArray().Select(v => v.GetProperty("name").GetString()!)];

	private static JsonElement Case(string section, string name) => Vectors.Load(VectorsPath).GetProperty(section).EnumerateArray().Single(v => v.GetProperty("name").GetString() == name);

	private static string FixturePath(string name) => Path.Combine(Vectors.RepoRoot(), "src", "shared", "import", "fixtures", name);

	private static string Fixture(string name) => File.ReadAllText(FixturePath(name));

	private static BookmarkImport Run(string parser, string text) => parser switch
	{
		"chromium" => ChromiumBookmarks.Parse(text),
		"netscape" => NetscapeBookmarks.Parse(text),
		"arc" => ArcSidebar.Parse(text),
		_ => throw new ArgumentException(parser),
	};

	private static Type ErrorType(string name) => name switch
	{
		"invalid-json" => typeof(JsonException),
		"chromium-format" => typeof(ChromiumBookmarksFormatException),
		"netscape-format" => typeof(NetscapeBookmarksFormatException),
		"arc-format" => typeof(ArcSidebarFormatException),
		_ => throw new ArgumentException(name),
	};

	private string Write(string name, byte[] bytes)
	{
		var path = Path.Combine(_dir, name);
		File.WriteAllBytes(path, bytes);
		return path;
	}

	[Theory]
	[MemberData(nameof(ParseCases))]
	public void ReadsWhatTheTypeScriptParsersRead(string name)
	{
		var vector = Case("parse", name);

		var result = Run(vector.GetProperty("parser").GetString()!, Fixture(vector.GetProperty("fixture").GetString()!));

		var want = JsonNode.Parse(vector.GetProperty("expected").GetRawText());
		var got = JsonSerializer.SerializeToNode(result, ZingJson.Options);
		Assert.True(JsonNode.DeepEquals(want, got), $"expected {want?.ToJsonString()}\nactual   {got?.ToJsonString()}");
	}

	[Theory]
	[MemberData(nameof(RefusedCases))]
	public void RefusesWhatTheTypeScriptParsersRefuse(string name)
	{
		var vector = Case("refused", name);

		var error = Record.Exception(() => Run(vector.GetProperty("parser").GetString()!, Fixture(vector.GetProperty("fixture").GetString()!)));

		Assert.IsAssignableFrom(ErrorType(vector.GetProperty("error").GetString()!), error);
		if (vector.TryGetProperty("message", out var message)) Assert.Equal(message.GetString(), error.Message);
	}

	private static string SidebarOf(int tabs)
	{
		var ids = string.Join(",", Enumerable.Range(0, tabs).Select(i => $"\"t{i}\""));
		var items = string.Join(",", Enumerable.Range(0, tabs).Select(i => $"\"t{i}\",{{\"id\":\"t{i}\",\"title\":\"T{i}\",\"data\":{{\"tab\":{{\"savedURL\":\"https://example.com/\"}}}}}}"));
		return $"{{\"sidebar\":{{\"containers\":[{{\"spaces\":[\"s1\",{{\"id\":\"s1\",\"title\":\"Big\",\"containerIDs\":[\"pinned\",\"p\"]}}],\"items\":[\"p\",{{\"id\":\"p\",\"childrenIds\":[{ids}],\"data\":{{\"itemContainer\":{{}}}}}},{items}]}}]}}}}";
	}

	[Fact]
	public void AnArcSidebarExactlyAtTheItemCapStillParses()
	{
		var result = ArcSidebar.Parse(SidebarOf(50_000));

		Assert.Equal(50_000, BookmarkImport.CountLinks(result.Other));
	}

	[Fact]
	public void AnArcSidebarJustOverTheItemCapIsRefused()
	{
		var error = Assert.Throws<ArcSidebarFormatException>(() => ArcSidebar.Parse(SidebarOf(50_001)));

		Assert.Equal("Arc's sidebar has too many items", error.Message);
	}

	[Fact]
	public void SafarisFavoritesFolderIsNotTheBarBecauseItCarriesNoToolbarMark()
	{
		var result = NetscapeBookmarks.Parse(Fixture("netscape-safari.html"));

		Assert.Empty(result.Bar);
		Assert.Equal(["Favorites", "Reading"], result.Other.Select(n => n.Title));
	}

	[Theory]
	[InlineData("https://a.example/", true)]
	[InlineData("http://a.example/x?y=1", true)]
	[InlineData("javascript:alert(1)", false)]
	[InlineData("chrome://settings", false)]
	[InlineData("file:///C:/a.txt", false)]
	[InlineData("a.example", false)]
	[InlineData("", false)]
	public void OnlyHttpAddressesBecomeBookmarks(string url, bool accepted) => Assert.Equal(accepted, BookmarkImport.AcceptsBookmarkUrl(url));

	[Fact]
	public void CountsLinksAtEveryDepth()
	{
		var result = ChromiumBookmarks.Parse(Fixture("chromium-bookmarks.json"));

		Assert.Equal(3, BookmarkImport.CountLinks(result.Bar));
		Assert.Equal(2, BookmarkImport.CountLinks(result.Other));
	}

	[Fact]
	public void DecodesNumericAndNamedEntities()
	{
		Assert.Equal("A<\"&unknown;", NetscapeBookmarks.DecodeEntities("&#x41;&lt;&quot;&unknown;"));
	}

	[Theory]
	[InlineData("chromium-bookmarks.json", "chromium")]
	[InlineData("arc-sidebar.json", "arc")]
	[InlineData("netscape-chrome.html", "netscape")]
	[InlineData("netscape-safari.html", "netscape")]
	[InlineData("netscape-firefox.html", "netscape")]
	public void SniffsTheFormatFromTheContentNotTheExtension(string fixture, string parser)
	{
		var path = Path.Combine(_dir, "renamed.dat");
		File.Copy(FixturePath(fixture), path);

		var result = ImportFile.ParseBookmarks(path);

		Assert.Equivalent(Run(parser, Fixture(fixture)), result);
	}

	[Fact]
	public void AnArcSidebarWithATabTitledRootsIsStillReadAsArc()
	{
		var text = Fixture("arc-sidebar.json").Replace("\"title\": \"Renamed\"", "\"title\": \"roots\"", StringComparison.Ordinal);
		Assert.Contains("\"roots\"", text);
		var path = Write("StorableSidebar.json", System.Text.Encoding.UTF8.GetBytes(text));

		Assert.Equivalent(ArcSidebar.Parse(text), ImportFile.ParseBookmarks(path));
	}

	[Fact]
	public void ReadsBookmarksNestedDeeperThanTheJsonDefaultLimit()
	{
		var folder = "{\"type\":\"url\",\"name\":\"Deep\",\"url\":\"https://deep.example/\"}";
		for (var i = 0; i < 60; i++) folder = $"{{\"type\":\"folder\",\"name\":\"L{i}\",\"children\":[{folder}]}}";
		var json = $"{{\"roots\":{{\"bookmark_bar\":{{\"type\":\"folder\",\"children\":[{folder}]}}}}}}";
		var path = Write("Bookmarks", System.Text.Encoding.UTF8.GetBytes(json));

		Assert.Equal(1, BookmarkImport.CountLinks(ChromiumBookmarks.Parse(json).Bar));
		Assert.Equal(1, BookmarkImport.CountLinks(ImportFile.ParseBookmarks(path).Bar));
	}

	[Fact]
	public void SniffingToleratesABomAndLeadingWhitespace()
	{
		var path = Write("bom.json", [0xEF, 0xBB, 0xBF, .. "\n  "u8.ToArray(), .. System.Text.Encoding.UTF8.GetBytes(Fixture("chromium-bookmarks.json"))]);

		Assert.Equal(3, BookmarkImport.CountLinks(ImportFile.ParseBookmarks(path).Bar));
	}

	[Theory]
	[InlineData("not-bookmarks.txt")]
	[InlineData("not-chromium.json")]
	public void RefusesAFileItCannotRecogniseByName(string fixture)
	{
		var path = Path.Combine(_dir, fixture);
		File.Copy(FixturePath(fixture), path);

		var error = Assert.Throws<UnknownBookmarksFileException>(() => ImportFile.ParseBookmarks(path));

		Assert.Contains(fixture, error.Message);
	}

	[Fact]
	public void MalformedJsonIsReportedAsDamaged()
	{
		var path = Path.Combine(_dir, "truncated.json");
		File.Copy(FixturePath("invalid.json"), path);

		Assert.Throws<BookmarksFileDamagedException>(() => ImportFile.ParseBookmarks(path));
	}

	[Fact]
	public void TruncatedJsonWithoutEitherKeyAndTooDeepJsonAreDamagedToo()
	{
		var broken = Write("broken.json", "{\"x\": "u8.ToArray());
		var deep = Write("deep.json", System.Text.Encoding.UTF8.GetBytes("{\"roots\":" + new string('[', 300) + new string(']', 300) + "}"));

		Assert.Throws<BookmarksFileDamagedException>(() => ImportFile.ParseBookmarks(broken));
		Assert.Throws<BookmarksFileDamagedException>(() => ImportFile.ParseBookmarks(deep));
	}

	[Fact]
	public void AFileOverTheLimitIsRefusedBeforeItIsRead()
	{
		var path = Path.Combine(_dir, "big.html");
		using (var file = File.Create(path)) file.SetLength(ImportFile.MaxBytes + 1);

		var error = Assert.Throws<ImportFileTooLargeException>(() => ImportFile.ReadText(path));

		Assert.Contains("10 MB", error.Message);
	}

	[Fact]
	public void AWindows1252FileIsRefusedAsNotUtf8()
	{
		var path = Write("latin.html", [.. "<!DOCTYPE NETSCAPE-Bookmark-file-1><DL><DT><A HREF=\"https://a.example/\">Caf"u8.ToArray(), 0xE9, .. "</A></DL>"u8.ToArray()]);

		Assert.Throws<ImportFileNotUtf8Exception>(() => ImportFile.ReadText(path));
		Assert.Throws<ImportFileNotUtf8Exception>(() => ImportFile.ParseBookmarks(path));
	}

	[Fact]
	public void TheVaultsReaderKeepsItsOwnErrorsOverTheSharedCheck()
	{
		var big = Path.Combine(_dir, "big.csv");
		using (var file = File.Create(big)) file.SetLength(ImportFile.MaxBytes + 1);
		var latin = Write("latin.csv", [0x6e, 0xe9, 0x2c]);

		Assert.Throws<CsvTooLargeException>(() => VaultCsv.ReadFile(big));
		Assert.Throws<CsvSyntaxException>(() => VaultCsv.ReadFile(latin));
	}
}
