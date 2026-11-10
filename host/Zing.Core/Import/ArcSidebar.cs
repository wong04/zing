using System.Text.Json;

namespace Zing.Core.Import;

public sealed class ArcSidebarFormatException(string message) : Exception(message);

/// <summary>
/// Arc keeps its sidebar in StorableSidebar.json. On Windows that's %LOCALAPPDATA%\Packages\TheBrowserCompany.Arc_&lt;id&gt;\LocalCache\Local\Arc\.
/// Spaces and items are stored as flat arrays that alternate an id string with the object it names. Each space lists its containers as
/// ["pinned", "&lt;containerId&gt;", "unpinned", "&lt;containerId&gt;"], and items point at their children through <c>childrenIds</c>.
/// This layout comes from community exporters, not a published spec, so every assumption fails loudly instead of importing half a sidebar.
/// The C# twin of src/shared/import/arcSidebar.ts, held to it by parsers.vectors.json.
/// </summary>
public static class ArcSidebar
{
	// Real sidebars nest a handful of folders; a file that nests deeper, or loops, would otherwise recurse until the process dies.
	private const int MaxDepth = 64;

	// childrenIds may name one item from many parents, so a tiny file can expand to millions of visits (20 levels listing the next twice is 2^20).
	private const int MaxItems = 50_000;

	private sealed class Visits
	{
		public int Count;
	}

	/// <exception cref="JsonException">Malformed JSON.</exception>
	/// <exception cref="ArcSidebarFormatException">Valid JSON that isn't the layout Zing knows.</exception>
	public static BookmarkImport Parse(string json)
	{
		using var document = JsonDocument.Parse(json, BookmarkImport.JsonOptions);
		return Parse(document.RootElement);
	}

	/// <exception cref="ArcSidebarFormatException">Not the layout Zing knows.</exception>
	public static BookmarkImport Parse(JsonElement root)
	{
		if (root.ValueKind != JsonValueKind.Object
			|| !root.TryGetProperty("sidebar", out var sidebar) || sidebar.ValueKind != JsonValueKind.Object
			|| !sidebar.TryGetProperty("containers", out var containers) || containers.ValueKind != JsonValueKind.Array)
			throw new ArcSidebarFormatException("No sidebar.containers array");
		var main = containers.EnumerateArray().FirstOrDefault(c => c.ValueKind == JsonValueKind.Object && c.TryGetProperty("spaces", out _) && c.TryGetProperty("items", out _));
		if (main.ValueKind != JsonValueKind.Object) throw new ArcSidebarFormatException("No container with spaces and items");
		Dictionary<string, JsonElement> items = [];
		foreach (var item in ObjectsOf(main.GetProperty("items"))) items[item.GetProperty("id").GetString()!] = item;
		List<string> skipped = [];
		List<ImportedNode> other = [];
		Visits visits = new();
		var index = 0;
		foreach (var space in ObjectsOf(main.GetProperty("spaces")))
		{
			index++;
			var title = TextOf(space, "title");
			var pinnedId = ContainerAfter(space, "pinned");
			if (string.IsNullOrEmpty(pinnedId))
			{
				skipped.Add($"Space \"{(space.TryGetProperty("title", out var raw) && raw.ValueKind != JsonValueKind.Null ? title : index.ToString())}\" has no pinned container");
				continue;
			}
			var children = items.TryGetValue(pinnedId, out var pinned) ? ConvertAll(pinned, items, skipped, [], visits) : [];
			other.Add(new ImportedFolder(title != "" ? title : $"Space {index}", children));
		}
		return new BookmarkImport(TopApps(main, items, skipped, visits), other, skipped);
	}

	private static List<JsonElement> ObjectsOf(JsonElement list)
	{
		if (list.ValueKind != JsonValueKind.Array) throw new ArcSidebarFormatException("Expected an array of alternating ids and objects");
		return [.. list.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)];
	}

	private static string TextOf(JsonElement element, string property) =>
		element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";

	private static string? ContainerAfter(JsonElement space, string label)
	{
		if (!space.TryGetProperty("containerIDs", out var ids) || ids.ValueKind != JsonValueKind.Array) return null;
		var list = ids.EnumerateArray().ToList();
		var at = list.FindIndex(id => id.ValueKind == JsonValueKind.String && id.GetString() == label);
		if (at == -1 || at + 1 >= list.Count) return null;
		return list[at + 1].ValueKind == JsonValueKind.String ? list[at + 1].GetString() : null;
	}

	private static List<string> ChildIds(JsonElement item)
	{
		if (!item.TryGetProperty("childrenIds", out var ids) || ids.ValueKind == JsonValueKind.Null) return [];
		if (ids.ValueKind != JsonValueKind.Array) throw new ArcSidebarFormatException($"Item {TextOf(item, "id")} has childrenIds that isn't an array");
		return [.. ids.EnumerateArray().Select(id => id.ValueKind == JsonValueKind.String ? id.GetString()! : id.GetRawText())];
	}

	/// <param name="path">The ids of the items this one sits inside.</param>
	private static List<ImportedNode> ConvertAll(JsonElement container, Dictionary<string, JsonElement> items, List<string> skipped, HashSet<string> path, Visits visits)
	{
		var id = TextOf(container, "id");
		if (!path.Add(id)) throw new ArcSidebarFormatException($"Item {id} is its own ancestor");
		if (path.Count > MaxDepth) throw new ArcSidebarFormatException("Arc's sidebar is nested too deeply");
		List<ImportedNode> children = [.. ChildIds(container).SelectMany(child => Convert(child, items, skipped, path, visits))];
		path.Remove(id);
		return children;
	}

	// Arc's Favorites (the icon grid at the top of the sidebar) are the closest thing to a Favourites bar.
	private static List<ImportedNode> TopApps(JsonElement main, Dictionary<string, JsonElement> items, List<string> skipped, Visits visits)
	{
		if (!main.TryGetProperty("topAppsContainerIDs", out var ids) || ids.ValueKind == JsonValueKind.Null) return [];
		if (ids.ValueKind != JsonValueKind.Array) throw new ArcSidebarFormatException("topAppsContainerIDs isn't an array");
		return [.. ids.EnumerateArray()
			.Where(id => id.ValueKind == JsonValueKind.String)
			.SelectMany(id => items.TryGetValue(id.GetString()!, out var container) ? ConvertAll(container, items, skipped, [], visits) : [])];
	}

	private static bool Present(JsonElement parent, string property, out JsonElement value) =>
		parent.TryGetProperty(property, out value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);

	private static List<ImportedNode> Convert(string id, Dictionary<string, JsonElement> items, List<string> skipped, HashSet<string> path, Visits visits)
	{
		if (++visits.Count > MaxItems) throw new ArcSidebarFormatException("Arc's sidebar has too many items");
		if (!items.TryGetValue(id, out var item))
		{
			skipped.Add($"Missing item {id}");
			return [];
		}
		var data = Present(item, "data", out var d) && d.ValueKind == JsonValueKind.Object ? d : default;
		if (data.ValueKind == JsonValueKind.Object && Present(data, "tab", out var tab) && tab.ValueKind == JsonValueKind.Object)
		{
			var url = TextOf(tab, "savedURL");
			var title = FirstNonEmpty(TextOf(item, "title"), TextOf(tab, "savedTitle"), url);
			if (!BookmarkImport.AcceptsBookmarkUrl(url))
			{
				skipped.Add($"{title}: {BookmarkImport.NotHttpReason}");
				return [];
			}
			return [new ImportedLink(title, url)];
		}
		if ((data.ValueKind == JsonValueKind.Object && Present(data, "list", out _)) || Present(item, "childrenIds", out _))
			return [new ImportedFolder(FirstNonEmpty(TextOf(item, "title"), "Folder"), ConvertAll(item, items, skipped, path, visits))];
		skipped.Add($"Item {id} is neither a tab nor a folder");
		return [];
	}

	private static string FirstNonEmpty(params string[] candidates) => candidates.FirstOrDefault(c => c != "") ?? "";
}
