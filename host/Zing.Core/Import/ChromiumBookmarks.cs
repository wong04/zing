using System.Text.Json;

namespace Zing.Core.Import;

public sealed class ChromiumBookmarksFormatException(string message) : Exception(message);

/// <summary>
/// Chrome, Edge, Brave and Dia keep bookmarks in a JSON file named <c>Bookmarks</c> inside each profile folder. The C# twin of
/// src/shared/import/chromiumBookmarks.ts, held to it by parsers.vectors.json.
/// </summary>
public static class ChromiumBookmarks
{
	/// <exception cref="JsonException">Malformed JSON.</exception>
	/// <exception cref="ChromiumBookmarksFormatException">Valid JSON that isn't a Bookmarks file.</exception>
	public static BookmarkImport Parse(string json)
	{
		using var document = JsonDocument.Parse(json, BookmarkImport.JsonOptions);
		return Parse(document.RootElement);
	}

	/// <exception cref="ChromiumBookmarksFormatException">Not a Bookmarks file.</exception>
	public static BookmarkImport Parse(JsonElement root)
	{
		if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("roots", out var roots) || roots.ValueKind != JsonValueKind.Object)
			throw new ChromiumBookmarksFormatException("No \"roots\" object; this is not a Chromium Bookmarks file");
		List<string> skipped = [];
		var bar = ConvertChildren(Root(roots, "bookmark_bar"), skipped);
		List<ImportedNode> other = [.. ConvertChildren(Root(roots, "other"), skipped)];
		var synced = Root(roots, "synced");
		var mobile = ConvertChildren(synced, skipped);
		if (mobile.Count > 0) other.Add(new ImportedFolder(NonEmpty(synced, "name") ?? "Mobile bookmarks", mobile));
		return new BookmarkImport(bar, other, skipped);
	}

	private static JsonElement? Root(JsonElement roots, string name) => roots.TryGetProperty(name, out var node) && node.ValueKind == JsonValueKind.Object ? node : null;

	private static string? NonEmpty(JsonElement? node, string property) =>
		node is { } n && n.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : null;

	private static List<ImportedNode> ConvertChildren(JsonElement? node, List<string> skipped)
	{
		List<ImportedNode> result = [];
		if (node is not { } n || !n.TryGetProperty("children", out var children) || children.ValueKind != JsonValueKind.Array) return result;
		foreach (var child in children.EnumerateArray()) result.AddRange(Convert(child, skipped));
		return result;
	}

	private static List<ImportedNode> Convert(JsonElement node, List<string> skipped)
	{
		if (node.ValueKind != JsonValueKind.Object) throw new ChromiumBookmarksFormatException("A bookmark entry isn't an object");
		var type = node.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
		var name = NonEmpty(node, "name");
		if (type == "folder") return [new ImportedFolder(name ?? "", ConvertChildren(node, skipped))];
		if (type != "url") throw new ChromiumBookmarksFormatException($"Unknown node type \"{type ?? "undefined"}\"");
		var url = node.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString()! : "";
		if (!BookmarkImport.AcceptsBookmarkUrl(url))
		{
			skipped.Add($"{name ?? url}: {BookmarkImport.NotHttpReason}");
			return [];
		}
		return [new ImportedLink(name ?? url, url)];
	}
}
