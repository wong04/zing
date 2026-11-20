using Zing.Core.Sync;

namespace Zing.Core.Tests.Sync;

/// <summary>The library's three-way merge: two PCs edit the copy they last synced, and the merge keeps both PCs' edits.</summary>
public class LibraryTreeTests
{
	private const long T = 1_000;

	private static BookmarkLink Link(string id, string url, string? title = null, long at = T) => new(id, title ?? id, url, at);

	private static BookmarkFolder Folder(string id, IReadOnlyList<BookmarkNode> children, string? title = null, long at = T, string? importedFrom = null) =>
		new(id, title ?? id, children, at, importedFrom);

	private static BrowserLibrary Lib(params BookmarkNode[] top) =>
		new([Folder(Library.FavouritesId, [], Library.FavouritesTitle), Folder(Library.PinnedId, [], Library.PinnedTitle), .. top], []);

	/// <summary>The merge as a sync makes it: this PC's copy matched against the server's, merged over the common copy, then made a valid tree.</summary>
	private static BrowserLibrary Sync(BrowserLibrary common, BrowserLibrary mine, BrowserLibrary theirs)
	{
		var b = LibraryTree.Flatten(common);
		var t = LibraryTree.Flatten(theirs);
		var m = LibraryTree.MatchNew(b, LibraryTree.Flatten(mine), t);
		return LibraryTree.Build(LibraryTree.Normalize(LibraryTree.Merge(b, m, t), T));
	}

	/// <summary>Each node as "parent/id:title", depth first, so a test reads the whole tree in one line.</summary>
	private static List<string> Shape(BrowserLibrary library)
	{
		var rows = new List<string>();
		void Walk(IReadOnlyList<BookmarkNode> nodes, string parent)
		{
			foreach (var node in nodes)
			{
				if (Library.IsFixedFolder(node.Id) && node is BookmarkFolder { Children.Count: 0 }) continue;
				rows.Add($"{parent}/{node.Id}:{node.Title}");
				if (node is BookmarkFolder folder) Walk(folder.Children, node.Id);
			}
		}
		Walk(library.Bookmarks, "");
		return rows;
	}

	[Fact]
	public void Flatten_then_build_gives_the_same_library()
	{
		var library = new BrowserLibrary(
			[Folder(Library.FavouritesId, [Link("a", "https://a.test/")]), Folder(Library.PinnedId, []), Folder("f", [Link("b", "https://b.test/"), Folder("g", [], importedFrom: "chrome:Default")])],
			[new ReadingItem("r", "R", "https://r.test/", 5, true, 7)]);

		var rebuilt = LibraryTree.Build(LibraryTree.Normalize(LibraryTree.Flatten(library), T));

		Assert.Equal(LibraryTree.Flatten(library), LibraryTree.Flatten(rebuilt), new EntriesComparer());
	}

	[Fact]
	public void A_rename_on_one_pc_and_a_move_on_the_other_both_survive()
	{
		var common = Lib(Folder("f", [Link("a", "https://a.test/")]), Folder("g", []));
		var renamed = Lib(Folder("f", [Link("a", "https://a.test/", "Renamed", at: T + 5)]), Folder("g", []));
		var moved = Lib(Folder("f", []), Folder("g", [Link("a", "https://a.test/")]));

		Assert.Equal(["/f:f", "/g:g", "g/a:Renamed"], Shape(Sync(common, renamed, moved)));
		Assert.Equal(["/f:f", "/g:g", "g/a:Renamed"], Shape(Sync(common, moved, renamed)));
	}

	[Fact]
	public void The_same_node_moved_to_two_folders_ends_up_in_one_of_them_once()
	{
		var common = Lib(Folder("f", []), Folder("g", []), Link("a", "https://a.test/"));
		var intoF = Lib(Folder("f", [Link("a", "https://a.test/")]), Folder("g", []));
		var intoG = Lib(Folder("f", []), Folder("g", [Link("a", "https://a.test/")]));

		var merged = Shape(Sync(common, intoF, intoG));

		Assert.Equal(["/f:f", "/g:g", "g/a:a"], merged);
	}

	[Fact]
	public void A_delete_loses_to_an_edit_the_deleting_pc_had_not_seen()
	{
		var common = Lib(Link("a", "https://a.test/"));
		var deleted = Lib();
		var renamed = Lib(Link("a", "https://a.test/", "Kept", at: T + 5));

		Assert.Equal(["/a:Kept"], Shape(Sync(common, deleted, renamed)));
		Assert.Equal(["/a:Kept"], Shape(Sync(common, renamed, deleted)));
	}

	[Fact]
	public void A_delete_of_a_node_the_other_pc_left_alone_sticks()
	{
		var common = Lib(Link("a", "https://a.test/"), Link("b", "https://b.test/"));
		var deleted = Lib(Link("b", "https://b.test/"));
		var other = Lib(Link("a", "https://a.test/"), Link("b", "https://b.test/", "B2", at: T + 5));

		Assert.Equal(["/b:B2"], Shape(Sync(common, deleted, other)));
		Assert.Equal(["/b:B2"], Shape(Sync(common, other, deleted)));
	}

	[Fact]
	public void A_bookmark_added_to_a_folder_another_pc_deleted_keeps_the_folder_with_just_that_bookmark()
	{
		var common = Lib(Folder("f", [Link("old", "https://old.test/")]));
		var deleted = Lib();
		var added = Lib(Folder("f", [Link("old", "https://old.test/"), Link("new", "https://new.test/")]));

		Assert.Equal(["/f:f", "f/new:new"], Shape(Sync(common, deleted, added)));
		Assert.Equal(["/f:f", "f/new:new"], Shape(Sync(common, added, deleted)));
	}

	[Fact]
	public void A_bookmark_moved_into_a_folder_the_other_pc_deleted_with_its_parent_lands_at_the_top()
	{
		var common = Lib(Folder("p", [Folder("f", [])]), Link("a", "https://a.test/"));
		var deleted = Lib(Link("a", "https://a.test/"));
		var moved = Lib(Folder("p", [Folder("f", [Link("a", "https://a.test/")])]));

		var merged = Shape(Sync(common, deleted, moved));

		Assert.Contains("/f:f", merged);
		Assert.Contains("f/a:a", merged);
		Assert.DoesNotContain(merged, row => row.StartsWith("/p:", StringComparison.Ordinal));
	}

	[Fact]
	public void Two_folders_moved_into_each_other_on_two_pcs_break_out_of_the_loop()
	{
		var common = Lib(Folder("x", []), Folder("y", []));
		var xInY = Lib(Folder("y", [Folder("x", [])]));
		var yInX = Lib(Folder("x", [Folder("y", [])]));

		var merged = Shape(Sync(common, xInY, yInX));

		Assert.Equal(["/x:x", "x/y:y"], merged);
	}

	[Fact]
	public void Bookmarks_added_to_one_folder_on_both_pcs_are_all_kept_in_place()
	{
		var common = Lib(Folder("f", [Link("a", "https://a.test/"), Link("z", "https://z.test/")]));
		var mine = Lib(Folder("f", [Link("a", "https://a.test/"), Link("m", "https://m.test/"), Link("z", "https://z.test/")]));
		var theirs = Lib(Folder("f", [Link("t", "https://t.test/"), Link("a", "https://a.test/"), Link("z", "https://z.test/")]));

		Assert.Equal(["/f:f", "f/t:t", "f/a:a", "f/m:m", "f/z:z"], Shape(Sync(common, mine, theirs)));
	}

	[Fact]
	public void A_reorder_on_one_pc_and_an_add_on_the_other_both_survive()
	{
		var common = Lib(Folder("f", [Link("a", "https://a.test/"), Link("b", "https://b.test/"), Link("c", "https://c.test/")]));
		var reordered = Lib(Folder("f", [Link("c", "https://c.test/"), Link("a", "https://a.test/"), Link("b", "https://b.test/")]));
		var added = Lib(Folder("f", [Link("a", "https://a.test/"), Link("n", "https://n.test/"), Link("b", "https://b.test/"), Link("c", "https://c.test/")]));

		Assert.Equal(["/f:f", "f/c:c", "f/a:a", "f/n:n", "f/b:b"], Shape(Sync(common, reordered, added)));
		Assert.Equal(["/f:f", "f/c:c", "f/a:a", "f/n:n", "f/b:b"], Shape(Sync(common, added, reordered)));
	}

	[Fact]
	public void A_rename_on_both_pcs_takes_the_later_one()
	{
		var common = Lib(Folder("f", []));
		var early = Lib(Folder("f", [], "Early", at: T + 1));
		var late = Lib(Folder("f", [], "Late", at: T + 9));

		Assert.Equal(["/f:Late"], Shape(Sync(common, early, late)));
		Assert.Equal(["/f:Late"], Shape(Sync(common, late, early)));
	}

	[Fact]
	public void A_first_sync_of_two_pcs_that_imported_the_same_browser_merges_instead_of_doubling()
	{
		var here = Lib(Folder("h1", [Link("h2", "https://a.test/"), Link("h3", "https://mine.test/")], "Imported from Chrome", importedFrom: "chrome:Default"));
		var there = Lib(Folder("t1", [Link("t2", "https://a.test/"), Link("t3", "https://theirs.test/")], "Imported from Chrome (Work)", importedFrom: "chrome:Default"));

		var merged = Shape(Sync(new BrowserLibrary([], []), here, there));

		Assert.Equal(["/t1:Imported from Chrome (Work)", "t1/t2:t2", "t1/t3:t3", "t1/h3:h3"], merged);
	}

	[Fact]
	public void A_first_sync_merges_the_favourites_bars_of_both_pcs()
	{
		var here = new BrowserLibrary([Folder(Library.FavouritesId, [Link("h", "https://same.test/"), Link("m", "https://mine.test/")]), Folder(Library.PinnedId, [])], []);
		var there = new BrowserLibrary([Folder(Library.FavouritesId, [Link("t", "https://same.test/")]), Folder(Library.PinnedId, [])], []);

		var bar = Library.Favourites(Sync(new BrowserLibrary([], []), here, there));

		Assert.Equal(["t", "m"], bar.Select(n => n.Id));
	}

	[Fact]
	public void Reading_list_edits_on_two_pcs_merge_field_by_field()
	{
		var item = new ReadingItem("r", "Read me", "https://r.test/", 1, false, T);
		var common = new BrowserLibrary([], [item]);
		var read = new BrowserLibrary([], [item with { IsRead = true, UpdatedAt = T + 1 }]);
		var added = new BrowserLibrary([], [new ReadingItem("n", "New", "https://n.test/", 2, false, T + 2), item]);

		var merged = Sync(common, read, added).ReadingList;

		Assert.Equal(["n", "r"], merged.Select(i => i.Id));
		Assert.True(merged.Single(i => i.Id == "r").IsRead);
	}

	[Fact]
	public void Every_node_has_one_shard_of_32()
	{
		var shards = Enumerable.Range(0, 2000).Select(i => LibraryTree.ShardOf($"id-{i}")).ToHashSet();

		Assert.Equal(LibraryTree.ShardCount, shards.Count);
		Assert.Equal(LibraryTree.ShardOf("same"), LibraryTree.ShardOf("same"));
	}

	private sealed class EntriesComparer : IEqualityComparer<Dictionary<string, LibraryEntry>>
	{
		public bool Equals(Dictionary<string, LibraryEntry>? a, Dictionary<string, LibraryEntry>? b) => a is not null && b is not null && LibraryTree.Same(a, b);

		public int GetHashCode(Dictionary<string, LibraryEntry> obj) => obj.Count;
	}
}
