using System.Text.Json;
using Zing.Core.Import;
using Zing.Core.Net;

namespace Zing.Core.Tests.Import;

/// <summary>import:export-bookmarks: the library written through the Save dialog, read back by the importer.</summary>
public sealed class BookmarkExportTests : IDisposable
{
	private readonly string _root = Directory.CreateTempSubdirectory("zing-export-").FullName;
	private readonly Bridge _bridge = new();
	private readonly FakeWindowHost _host = new();
	private readonly LibraryStore _library;
	private readonly ChromeApi _api;

	public BookmarkExportTests()
	{
		var library = Library.EmptyLibrary(1_700_000_000_000);
		library = Library.WithFavourites(library,
		[
			new BookmarkLink("f1", "GitHub", "https://github.com/", 1),
			new BookmarkFolder("f2", "Dev & <Docs>", [new BookmarkLink("f3", "MDN \"web\" docs", "https://developer.mozilla.org/?a=1&b=2", 1)], 1),
		], 1);
		var bookmarks = library.Bookmarks.ToList();
		var pinnedAt = bookmarks.FindIndex(n => n.Id == Library.PinnedId);
		bookmarks[pinnedAt] = ((BookmarkFolder)bookmarks[pinnedAt]) with { Children = [new BookmarkFolder("p1", "Work", [new BookmarkLink("p2", "Mail", "https://mail.example/", 1)], 1), new BookmarkLink("p3", "Calendar", "https://cal.example/", 1)] };
		bookmarks.Add(new BookmarkFolder("u1", "Reading — café", [new BookmarkFolder("u2", "Empty", [], 1), new BookmarkLink("u3", "News", "https://news.example/", 1)], 1, "chrome:Default"));
		bookmarks.Add(new BookmarkLink("u4", "Loose", "http://loose.example/", 1));
		_library = new LibraryStore(library with { Bookmarks = bookmarks, ReadingList = [Library.Reading("r1", "Later", "https://later.example/", 1)] }, _ => { }, Deterministic.Clock(), Deterministic.Ids("id-"));
		_api = new ChromeApi(_bridge, new AppServices(new SettingsStore(), _library, Address.FromDefaults(), new TickingClock(), Accounts.Guest(), new RelayConsent(), LocalAppData: () => _root), _host, "test");
	}

	public void Dispose()
	{
		_api.Dispose();
		Directory.Delete(_root, recursive: true);
	}

	// A null result has no value on the wire.
	private async Task<JsonElement?> Invoke(string channel)
	{
		var json = JsonSerializer.Serialize(new { kind = "invoke", id = 1, channel, args = Array.Empty<object>() });
		var result = JsonDocument.Parse(await _bridge.HandleAsync(json)).RootElement;
		if (!result.GetProperty("ok").GetBoolean()) throw new Xunit.Sdk.XunitException(result.GetProperty("error").ToString());
		return result.TryGetProperty("value", out var value) ? value : null;
	}

	private static ImportedNode Imported(BookmarkNode node) => node switch
	{
		BookmarkLink link => new ImportedLink(link.Title, link.Url),
		BookmarkFolder folder => new ImportedFolder(folder.Title, [.. folder.Children.Select(Imported)]),
		_ => throw new NotSupportedException(),
	};

	private static string Json(IEnumerable<ImportedNode> nodes) => JsonSerializer.Serialize(nodes.ToList(), ZingJson.Options);

	[Fact]
	public async Task ExportThenImportRoundTripsTheTree()
	{
		var path = Path.Combine(_root, "out.html");
		_host.SavePath = path;

		var outcome = (await Invoke("import:export-bookmarks"))!.Value;

		Assert.Equal(BookmarkExport.SuggestedFileName, _host.SuggestedSaveName);
		Assert.Equal("out.html", outcome.GetProperty("name").GetString());
		Assert.Equal(6, outcome.GetProperty("bookmarks").GetInt32());
		var read = ImportFile.ParseBookmarks(path);
		var snapshot = _library.Snapshot;
		Assert.Equal(Json(Library.Favourites(snapshot).Select(Imported)), Json(read.Bar));
		Assert.Equal(Json(snapshot.Bookmarks.Where(n => n.Id != Library.FavouritesId).Select(Imported)), Json(read.Other));
		Assert.Empty(read.Skipped);
		Assert.DoesNotContain("later.example", File.ReadAllText(path));
	}

	[Fact]
	public async Task ExportedFileImportsIntoAnEmptyLibraryWithTheSameTree()
	{
		var path = Path.Combine(_root, "out.html");
		_host.SavePath = path;
		await Invoke("import:export-bookmarks");
		var fresh = new LibraryStore(Library.EmptyLibrary(1), _ => { }, Deterministic.Clock(), Deterministic.Ids("new-"));

		var outcome = fresh.Import(ImportFile.ParseBookmarks(path), ImportApi.FileKey("out.html"), "out.html");

		Assert.Equal(Json(Library.Favourites(_library.Snapshot).Select(Imported)), Json(Library.Favourites(fresh.Snapshot).Select(Imported)));
		var folder = Assert.IsType<BookmarkFolder>(fresh.Snapshot.Bookmarks.Single(n => n.Title == outcome.FolderTitle));
		Assert.Equal(Json(_library.Snapshot.Bookmarks.Where(n => n.Id != Library.FavouritesId).Select(Imported)), Json(folder.Children.Select(Imported)));
	}

	[Fact]
	public async Task CancellingTheSaveDialogWritesNothing()
	{
		_host.SavePath = null;

		var outcome = await Invoke("import:export-bookmarks");

		Assert.True(outcome is null or { ValueKind: JsonValueKind.Null });
		Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
	}
}
