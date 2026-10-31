using System.Text.Json;
using System.Text.Json.Nodes;
using Zing.Core;

namespace Zing.Core.Tests;

public sealed class LibraryTests
{
	private static readonly JsonElement V = Vectors.Load("src/shared/library/library.vectors.json");

	private static BrowserLibrary Lib(JsonElement e) => e.Deserialize<BrowserLibrary>(ZingJson.Options)!;

	private static IReadOnlyList<BookmarkNode> Nodes(JsonElement e) => e.Deserialize<List<BookmarkNode>>(ZingJson.Options)!;

	private static IReadOnlyList<BookmarkNode> Tree => Lib(V.GetProperty("tree")).Bookmarks;

	private static IReadOnlyList<BookmarkNode> NodesOf(JsonElement c) => c.TryGetProperty("nodes", out var n) ? Nodes(n) : Tree;

	private static HashSet<string> Set(JsonElement list) => list.EnumerateArray().Select(i => i.GetString()!).ToHashSet();

	private static IEnumerable<JsonElement> Cases(string name) => V.GetProperty(name).EnumerateArray();

	internal static void AssertSame(JsonElement expected, object? actual, string because = "")
	{
		var want = DropNulls(JsonNode.Parse(expected.GetRawText()));
		var got = JsonSerializer.SerializeToNode(actual, ZingJson.Options);
		Assert.True(JsonNode.DeepEquals(want, got), $"{because}\nexpected {want?.ToJsonString()}\nactual   {got?.ToJsonString()}");
	}

	// The host omits nulls (the renderer reads `undefined`), so a vector's null means "absent".
	private static JsonNode? DropNulls(JsonNode? node)
	{
		if (node is JsonObject obj)
		{
			foreach (var key in obj.Where(p => p.Value is null).Select(p => p.Key).ToList()) obj.Remove(key);
			foreach (var value in obj.Select(p => p.Value).ToList()) DropNulls(value);
		}
		if (node is JsonArray array) foreach (var item in array) DropNulls(item);
		return node;
	}

	private static string Name(JsonElement c) => c.GetProperty("name").GetString()!;

	[Fact]
	public void A_fresh_library_has_a_favourites_folder()
	{
		var vector = V.GetProperty("emptyLibrary");
		var library = Library.EmptyLibrary(vector.GetProperty("now").GetInt64());
		AssertSame(vector.GetProperty("expected"), library);
		Assert.Equal(Library.FavouritesId, library.Bookmarks[0].Id);
	}

	[Fact]
	public void The_empty_library_matches_the_generated_defaults()
	{
		var defaults = ZingJson.LoadDefaults("library.json");
		var empty = JsonSerializer.SerializeToNode(Library.EmptyLibrary(0), ZingJson.Options);
		Assert.True(JsonNode.DeepEquals(defaults, empty), $"{defaults.ToJsonString()} vs {empty?.ToJsonString()}");
	}

	[Fact]
	public void With_favourites_replaces_or_creates_the_folder()
	{
		foreach (var c in Cases("withFavourites"))
		{
			var result = Library.WithFavourites(Lib(c.GetProperty("library")), Nodes(c.GetProperty("children")), c.GetProperty("now").GetInt64());
			AssertSame(c.GetProperty("expected"), result, Name(c));
		}
	}

	[Fact]
	public void Favourites_are_the_children_of_the_folder()
	{
		foreach (var c in Cases("favourites"))
			Assert.Equal(c.GetProperty("expected").EnumerateArray().Select(i => i.GetString()), Library.Favourites(Lib(c.GetProperty("library"))).Select(n => n.Id));
	}

	[Fact]
	public void Remove_works_at_any_depth_and_spares_favourites()
	{
		foreach (var c in Cases("remove"))
			AssertSame(c.GetProperty("expected"), Library.Remove(Set(c.GetProperty("ids")), Tree), Name(c));
	}

	[Fact]
	public void Rename_trims_ignores_blank_and_bumps_only_the_renamed_node()
	{
		foreach (var c in Cases("rename"))
		{
			var result = Library.Rename(c.GetProperty("id").GetString()!, c.GetProperty("title").GetString()!, NodesOf(c), c.GetProperty("now").GetInt64());
			AssertSame(c.GetProperty("expected"), result, Name(c));
		}
	}

	[Fact]
	public void Find_looks_in_folders()
	{
		foreach (var c in Cases("find"))
			Assert.Equal(c.GetProperty("expected").GetString(), Library.Find(c.GetProperty("id").GetString()!, Tree)?.Title);
	}

	[Fact]
	public void Collect_is_depth_first_and_takes_selected_folders_whole()
	{
		foreach (var c in Cases("collect"))
			Assert.Equal(c.GetProperty("expected").EnumerateArray().Select(i => i.GetString()), Library.Collect(Set(c.GetProperty("ids")), Tree).Select(n => n.Id));
	}

	[Fact]
	public void Urls_are_every_link_depth_first()
	{
		foreach (var c in Cases("urls"))
		{
			var node = Library.Find(c.GetProperty("id").GetString()!, NodesOf(c))!;
			Assert.Equal(c.GetProperty("expected").EnumerateArray().Select(i => i.GetString()), Library.Urls(node));
		}
	}

	[Fact]
	public void Group_into_folder_places_the_folder_where_the_first_item_was()
	{
		foreach (var c in Cases("groupIntoFolder"))
		{
			var result = Library.GroupIntoFolder(Set(c.GetProperty("ids")), c.GetProperty("title").GetString()!, Tree, c.GetProperty("newId").GetString()!, c.GetProperty("now").GetInt64());
			AssertSame(c.GetProperty("expected"), result, Name(c));
		}
	}

	[Fact]
	public void Visible_rows_follow_the_open_folders()
	{
		foreach (var c in Cases("visibleRows"))
		{
			var rows = Library.VisibleRows(Tree, Set(c.GetProperty("expanded"))).Select(r => new { title = r.Node.Title, depth = r.Depth });
			AssertSame(c.GetProperty("expected"), rows, Name(c));
		}
	}

	[Fact]
	public void Move_uses_insertion_point_semantics()
	{
		foreach (var c in Cases("move"))
		{
			var list = c.GetProperty("list").EnumerateArray().Select(i => i.GetString()!).ToList();
			var moved = Library.Move(list, c.GetProperty("from").GetInt32(), c.GetProperty("to").GetInt32());
			Assert.Equal(c.GetProperty("expected").EnumerateArray().Select(i => i.GetString()), moved);
		}
	}

	[Fact]
	public void A_serialised_library_has_exactly_the_typescript_shape_and_parses_back()
	{
		var vector = V.GetProperty("serialised").GetProperty("library");
		var library = Lib(vector);
		AssertSame(vector, library);
		var reparsed = JsonSerializer.Deserialize<BrowserLibrary>(JsonSerializer.Serialize(library, ZingJson.Options), ZingJson.Options)!;
		AssertSame(vector, reparsed);
	}

	[Fact]
	public void A_folder_is_told_from_a_link_by_having_children()
	{
		var nodes = Nodes(JsonDocument.Parse("""[{"id":"f","title":"F","children":[],"updatedAt":1},{"id":"l","title":"L","url":"u","updatedAt":1}]""").RootElement);
		Assert.IsType<BookmarkFolder>(nodes[0]);
		Assert.IsType<BookmarkLink>(nodes[1]);
	}
}
