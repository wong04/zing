using Zing.Core;

namespace Zing.Core.Tests;

public sealed class BookmarkIconsTests : IDisposable
{
	private readonly string _root = Directory.CreateTempSubdirectory("zing-icons-").FullName;

	private string Folder => Path.Combine(_root, "bookmark-icons");

	public void Dispose() => Directory.Delete(_root, true);

	private static string PngUrl(params byte[] body) =>
		"data:image/png;base64," + Convert.ToBase64String([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. body]);

	private static BrowserLibrary LibraryOf(params (string Id, string Url)[] links)
	{
		var library = Library.EmptyLibrary(1);
		var favourites = links.Take(1).Select(l => (BookmarkNode)Library.Link(l.Id, l.Id, l.Url, 1)).ToList();
		var rest = links.Skip(1).Select(l => (BookmarkNode)Library.Link(l.Id, l.Id, l.Url, 1)).ToList();
		var withBar = Library.WithFavourites(library, favourites, 1);
		return withBar with { Bookmarks = [.. withBar.Bookmarks, Library.Folder("folder", "Folder", rest, 1)] };
	}

	private int PngFiles() => Directory.Exists(Folder) ? Directory.GetFiles(Folder, "*.png").Length : 0;

	[Theory]
	[InlineData("https://example.com/", "https://example.com")]
	[InlineData("https://example.com/a/", "http://www.example.com/a")]
	[InlineData("https://Example.COM/a?x=1#top", "https://example.com/a?y=2")]
	[InlineData("https://example.com:443/a", "http://example.com/a")]
	public void Addresses_that_differ_only_in_scheme_www_trailing_slash_query_or_fragment_match(string bookmark, string page) =>
		Assert.Equal(BookmarkIcons.MatchKey(bookmark), BookmarkIcons.MatchKey(page));

	[Theory]
	[InlineData("https://mail.google.com/mail/u/0/", "https://docs.google.com/mail/u/0/")]
	[InlineData("https://docs.google.com/document/d/1", "https://docs.google.com/spreadsheets/d/1")]
	[InlineData("https://google.com/", "https://google.com/maps")]
	[InlineData("https://example.com:8080/", "https://example.com/")]
	[InlineData("https://example.com/A", "https://example.com/a")]
	public void Another_host_path_or_port_never_matches(string bookmark, string page) =>
		Assert.NotEqual(BookmarkIcons.MatchKey(bookmark), BookmarkIcons.MatchKey(page));

	[Theory]
	[InlineData("about:blank")]
	[InlineData("file:///C:/a.html")]
	[InlineData("data:text/html,hi")]
	[InlineData("not a url")]
	public void Only_web_pages_have_a_key(string url) => Assert.Null(BookmarkIcons.MatchKey(url));

	[Fact]
	public void A_visited_bookmarked_page_stores_its_icon_and_a_new_store_reads_it_back()
	{
		var library = LibraryOf(("bar", "https://example.com/"), ("deep", "https://news.example.org/today"));
		var icons = new BookmarkIcons(Folder);

		Assert.True(icons.Visited(library, "https://example.com/?from=bar", PngUrl(1, 2, 3)));
		Assert.True(icons.Visited(library, "https://news.example.org/today", PngUrl(4)));

		var reopened = new BookmarkIcons(Folder);
		var shown = reopened.ForLibrary(library);
		Assert.Equal(PngUrl(1, 2, 3), shown["bar"]);
		Assert.Equal(PngUrl(4), shown["deep"]);
	}

	[Fact]
	public void A_page_no_bookmark_matches_stores_nothing()
	{
		var library = LibraryOf(("docs", "https://docs.google.com/document/d/1"));
		var icons = new BookmarkIcons(Folder);

		Assert.False(icons.Visited(library, "https://mail.google.com/mail/u/0/", PngUrl(9)));
		Assert.False(icons.Visited(library, "https://docs.google.com/spreadsheets/d/1", PngUrl(9)));

		Assert.Empty(icons.ForLibrary(library));
		Assert.Equal(0, PngFiles());
	}

	[Fact]
	public void An_unvisited_bookmark_has_no_icon()
	{
		var library = LibraryOf(("seen", "https://seen.example/"), ("unseen", "https://unseen.example/"));
		var icons = new BookmarkIcons(Folder);
		icons.Visited(library, "https://seen.example/", PngUrl(1));

		Assert.Equal(["seen"], icons.ForLibrary(library).Keys);
	}

	[Fact]
	public void A_later_visit_with_a_changed_icon_replaces_it_and_the_same_icon_writes_nothing()
	{
		var library = LibraryOf(("bar", "https://example.com/"));
		var icons = new BookmarkIcons(Folder);
		var changes = 0;
		icons.Subscribe(() => changes++);

		Assert.True(icons.Visited(library, "https://example.com/", PngUrl(1)));
		Assert.False(icons.Visited(library, "https://example.com/", PngUrl(1)));
		Assert.True(icons.Visited(library, "https://example.com/", PngUrl(2)));

		Assert.Equal(2, changes);
		Assert.Equal(PngUrl(2), new BookmarkIcons(Folder).ForLibrary(library)["bar"]);
		Assert.Equal(1, PngFiles());
	}

	[Fact]
	public void An_icon_over_the_cap_or_not_a_png_is_not_stored_and_keeps_the_old_one()
	{
		var library = LibraryOf(("bar", "https://example.com/"));
		var icons = new BookmarkIcons(Folder);
		icons.Visited(library, "https://example.com/", PngUrl(1));

		Assert.False(icons.Visited(library, "https://example.com/", PngUrl(new byte[BookmarkIcons.MaxBytes])));
		Assert.False(icons.Visited(library, "https://example.com/", "data:image/png;base64," + Convert.ToBase64String("GIF89a"u8.ToArray())));
		Assert.False(icons.Visited(library, "https://example.com/", "data:image/png;base64,not base64!"));
		Assert.False(icons.Visited(library, "https://example.com/", "https://example.com/favicon.ico"));

		Assert.Equal(PngUrl(1), icons.ForLibrary(library)["bar"]);
	}

	[Fact]
	public void An_icon_right_at_the_cap_is_stored()
	{
		var library = LibraryOf(("bar", "https://example.com/"));
		var icons = new BookmarkIcons(Folder);

		Assert.True(icons.Visited(library, "https://example.com/", PngUrl(new byte[BookmarkIcons.MaxBytes - 8])));
	}

	[Fact]
	public void Deleting_the_bookmark_deletes_its_icon_but_keeps_one_another_bookmark_shares()
	{
		var library = LibraryOf(("bar", "https://example.com/"), ("copy", "https://example.com"), ("other", "https://other.example/"));
		var icons = new BookmarkIcons(Folder);
		icons.Visited(library, "https://example.com/", PngUrl(1));
		icons.Visited(library, "https://other.example/", PngUrl(2));
		var changes = 0;
		icons.Subscribe(() => changes++);

		var withoutOther = library with { Bookmarks = Library.Remove(new HashSet<string> { "other" }, library.Bookmarks) };
		icons.Prune(withoutOther);
		Assert.Equal(1, PngFiles());
		Assert.Equal(1, changes);

		var withoutBar = withoutOther with { Bookmarks = Library.Remove(new HashSet<string> { "bar" }, withoutOther.Bookmarks) };
		icons.Prune(withoutBar);
		Assert.Equal(PngUrl(1), icons.ForLibrary(withoutBar)["copy"]);

		var empty = withoutBar with { Bookmarks = Library.Remove(new HashSet<string> { "copy" }, withoutBar.Bookmarks) };
		icons.Prune(empty);
		Assert.Equal(0, PngFiles());
		Assert.Empty(new BookmarkIcons(Folder).ForLibrary(library));
	}

	[Fact]
	public void Pointing_a_bookmark_at_another_address_drops_the_old_icon()
	{
		var library = LibraryOf(("bar", "https://example.com/"));
		var icons = new BookmarkIcons(Folder);
		icons.Visited(library, "https://example.com/", PngUrl(1));

		var moved = LibraryOf(("bar", "https://elsewhere.example/"));
		icons.Prune(moved);

		Assert.Empty(icons.ForLibrary(moved));
		Assert.Equal(0, PngFiles());
	}

	[Fact]
	public void Pruning_before_any_icon_was_stored_creates_nothing()
	{
		new BookmarkIcons(Folder).Prune(LibraryOf(("bar", "https://example.com/")));

		Assert.False(Directory.Exists(Folder));
	}

	[Fact]
	public void The_write_is_atomic_and_leaves_no_temp_file()
	{
		var library = LibraryOf(("bar", "https://example.com/"));
		new BookmarkIcons(Folder).Visited(library, "https://example.com/", PngUrl(1));

		Assert.Equal([BookmarkIcons.FileName(BookmarkIcons.MatchKey("https://example.com/")!)], Directory.GetFiles(Folder).Select(Path.GetFileName));
	}
}
