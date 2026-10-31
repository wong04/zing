using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zing.Core;

/// <summary>A bookmark or a folder (src/shared/library/library.ts BookmarkNode). On the wire a folder is the node with a `children` list.</summary>
[JsonConverter(typeof(BookmarkNodeConverter))]
public abstract record BookmarkNode(string Id, string Title, long UpdatedAt);

public sealed record BookmarkLink(string Id, string Title, string Url, long UpdatedAt) : BookmarkNode(Id, Title, UpdatedAt);

public sealed record BookmarkFolder(string Id, string Title, IReadOnlyList<BookmarkNode> Children, long UpdatedAt) : BookmarkNode(Id, Title, UpdatedAt);

public sealed record ReadingItem(string Id, string Title, string Url, long Added, bool IsRead, long UpdatedAt);

/// <summary>The first bookmark is always the Favourites folder, whose children the Favourites bar shows.</summary>
public sealed record BrowserLibrary(IReadOnlyList<BookmarkNode> Bookmarks, IReadOnlyList<ReadingItem> ReadingList);

public sealed record VisibleRow(BookmarkNode Node, int Depth);

public sealed record GroupResult(IReadOnlyList<BookmarkNode> Nodes, string? FolderId);

/// <summary>Writes the TS shape field for field (id, title, url | children, updatedAt) and reads it back with the same strictness as parseNode.</summary>
public sealed class BookmarkNodeConverter : JsonConverter<BookmarkNode>
{
	public override BookmarkNode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
	{
		using var document = JsonDocument.ParseValue(ref reader);
		return Library.ParseNode(document.RootElement, "node");
	}

	public override void Write(Utf8JsonWriter writer, BookmarkNode value, JsonSerializerOptions options)
	{
		writer.WriteStartObject();
		writer.WriteString("id", value.Id);
		writer.WriteString("title", value.Title);
		switch (value)
		{
			case BookmarkLink link:
				writer.WriteString("url", link.Url);
				break;
			case BookmarkFolder folder:
				writer.WriteStartArray("children");
				foreach (var child in folder.Children) Write(writer, child, options);
				writer.WriteEndArray();
				break;
			default:
				throw new NotSupportedException($"Unknown bookmark node {value.GetType().Name}");
		}
		writer.WriteNumber("updatedAt", value.UpdatedAt);
		writer.WriteEndObject();
	}
}

/// <summary>Ported from src/shared/library/library.ts. Ids and times are passed in so callers (and tests) control them.</summary>
public static class Library
{
	/// <summary>Fixed so the Favourites folder can always be found, renamed or not.</summary>
	public const string FavouritesId = "fab00000-0000-4000-8000-000000000001";

	public const string FavouritesTitle = "Favourites";

	public static BookmarkLink Link(string id, string title, string url, long now) => new(id, title, url, now);

	public static BookmarkFolder Folder(string id, string title, IReadOnlyList<BookmarkNode> children, long now) => new(id, title, children, now);

	public static ReadingItem Reading(string id, string title, string url, long now) => new(id, title, url, now, false, now);

	public static BrowserLibrary EmptyLibrary(long now) => new([Folder(FavouritesId, FavouritesTitle, [], now)], []);

	public static IReadOnlyList<BookmarkNode> Favourites(BrowserLibrary library) =>
		library.Bookmarks.FirstOrDefault(n => n.Id == FavouritesId) is BookmarkFolder fav ? fav.Children : [];

	public static BrowserLibrary WithFavourites(BrowserLibrary library, IReadOnlyList<BookmarkNode> children, long now)
	{
		var bookmarks = library.Bookmarks.ToList();
		var index = bookmarks.FindIndex(n => n.Id == FavouritesId);
		if (index == -1) return library with { Bookmarks = [Folder(FavouritesId, FavouritesTitle, children, now), .. bookmarks] };
		bookmarks[index] = bookmarks[index] is BookmarkFolder fav
			? fav with { Children = children }
			: throw new InvalidDataException("The Favourites node is not a folder");
		return library with { Bookmarks = bookmarks };
	}

	/// <summary>Removes every node whose id is in `ids`, at any depth. The Favourites folder can't be removed: the bar would have nowhere to live.</summary>
	public static IReadOnlyList<BookmarkNode> Remove(IReadOnlySet<string> ids, IReadOnlyList<BookmarkNode> nodes)
	{
		var result = new List<BookmarkNode>();
		foreach (var node in nodes)
		{
			if (ids.Contains(node.Id) && node.Id != FavouritesId) continue;
			result.Add(node is BookmarkFolder folder ? folder with { Children = Remove(ids, folder.Children) } : node);
		}
		return result;
	}

	public static IReadOnlyList<BookmarkNode> Rename(string id, string title, IReadOnlyList<BookmarkNode> nodes, long now)
	{
		var trimmed = title.Trim();
		if (trimmed.Length == 0) return nodes;
		return nodes.Select(node =>
		{
			var copy = node.Id == id ? node with { Title = trimmed, UpdatedAt = now } : node;
			return copy is BookmarkFolder folder ? folder with { Children = Rename(id, trimmed, folder.Children, now) } : copy;
		}).ToList();
	}

	public static BookmarkNode? Find(string id, IReadOnlyList<BookmarkNode> nodes)
	{
		foreach (var node in nodes)
		{
			if (node.Id == id) return node;
			if (node is not BookmarkFolder folder) continue;
			var hit = Find(id, folder.Children);
			if (hit is not null) return hit;
		}
		return null;
	}

	/// <summary>Depth-first, in order. A matching folder is taken whole, not searched.</summary>
	public static IReadOnlyList<BookmarkNode> Collect(IReadOnlySet<string> ids, IReadOnlyList<BookmarkNode> nodes)
	{
		var result = new List<BookmarkNode>();
		foreach (var node in nodes)
		{
			if (ids.Contains(node.Id))
			{
				result.Add(node);
				continue;
			}
			if (node is BookmarkFolder folder) result.AddRange(Collect(ids, folder.Children));
		}
		return result;
	}

	/// <summary>
	/// Wraps the selected items in a new folder, placed where the first of them was. Selected items in different
	/// folders are gathered together; the folder lands in the parent of the first.
	/// </summary>
	public static GroupResult GroupIntoFolder(IReadOnlySet<string> ids, string title, IReadOnlyList<BookmarkNode> nodes, string newId, long now)
	{
		var selected = Collect(ids, nodes).Where(n => n.Id != FavouritesId).ToList();
		if (selected.Count == 0) return new GroupResult(nodes, null);
		var wrapper = Folder(newId, title, selected, now);
		var placed = false;
		IReadOnlyList<BookmarkNode> Rebuild(IReadOnlyList<BookmarkNode> list)
		{
			var result = new List<BookmarkNode>();
			foreach (var node in list)
			{
				if (ids.Contains(node.Id) && node.Id != FavouritesId)
				{
					if (!placed)
					{
						result.Add(wrapper);
						placed = true;
					}
					continue;
				}
				result.Add(node is BookmarkFolder folder ? folder with { Children = Rebuild(folder.Children) } : node);
			}
			return result;
		}
		return new GroupResult(Rebuild(nodes), wrapper.Id);
	}

	/// <summary>Every link in a folder, depth-first: what Ctrl-clicking a Favourites bar folder opens.</summary>
	public static IReadOnlyList<string> Urls(BookmarkNode node) => node switch
	{
		BookmarkLink link => [link.Url],
		BookmarkFolder folder => folder.Children.SelectMany(Urls).ToList(),
		_ => throw new NotSupportedException($"Unknown bookmark node {node.GetType().Name}"),
	};

	/// <summary>The rows a tree shows with some folders open, in display order: what the arrow keys move through.</summary>
	public static IReadOnlyList<VisibleRow> VisibleRows(IReadOnlyList<BookmarkNode> nodes, IReadOnlySet<string> expanded, int depth = 0)
	{
		var rows = new List<VisibleRow>();
		foreach (var node in nodes)
		{
			rows.Add(new VisibleRow(node, depth));
			if (node is BookmarkFolder folder && expanded.Contains(node.Id)) rows.AddRange(VisibleRows(folder.Children, expanded, depth + 1));
		}
		return rows;
	}

	/// <summary>`to` is an insertion point in the original list ("before the item now at `to`"), the same convention as SwiftUI's onMove.</summary>
	public static List<T> Move<T>(IReadOnlyList<T> list, int from, int to)
	{
		var inRange = from >= 0 && from < list.Count && to >= 0 && to <= list.Count;
		var copy = list.ToList();
		if (!inRange || to == from || to == from + 1) return copy;
		var item = copy[from];
		copy.RemoveAt(from);
		copy.Insert(to > from ? to - 1 : to, item);
		return copy;
	}

	public static BookmarkNode ParseNode(JsonElement raw, string where)
	{
		if (raw.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"{where} is not an object");
		var id = Str(raw, "id");
		var title = Str(raw, "title");
		if (id is null || title is null || !TryLong(raw, "updatedAt", out var updatedAt))
			throw new InvalidDataException($"{where} needs a string id and title and a numeric updatedAt");
		if (raw.TryGetProperty("children", out var children))
		{
			if (children.ValueKind != JsonValueKind.Array) throw new InvalidDataException($"{where}.children is not a list");
			var parsed = children.EnumerateArray().Select((c, i) => ParseNode(c, $"{where}.children[{i}]")).ToList();
			return new BookmarkFolder(id, title, parsed, updatedAt);
		}
		return new BookmarkLink(id, title, Str(raw, "url") ?? throw new InvalidDataException($"{where} has no url"), updatedAt);
	}

	public static ReadingItem ParseReading(JsonElement raw, string where)
	{
		if (raw.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"{where} is not an object");
		var id = Str(raw, "id");
		var title = Str(raw, "title");
		var url = Str(raw, "url");
		if (id is null || title is null || url is null || !TryLong(raw, "added", out var added) || !TryBool(raw, "isRead", out var isRead) || !TryLong(raw, "updatedAt", out var updatedAt))
			throw new InvalidDataException($"{where} is not a reading list item");
		return new ReadingItem(id, title, url, added, isRead, updatedAt);
	}

	public static string? Str(JsonElement obj, string name) =>
		obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

	public static bool TryLong(JsonElement obj, string name, out long value)
	{
		value = 0;
		return obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out value);
	}

	public static bool TryBool(JsonElement obj, string name, out bool value)
	{
		value = false;
		if (!obj.TryGetProperty(name, out var v) || v.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
		value = v.GetBoolean();
		return true;
	}
}
