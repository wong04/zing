using System.Globalization;
using System.Text.RegularExpressions;

namespace Zing.Core.Import;

public sealed class NetscapeBookmarksFormatException(string message) : Exception(message);

/// <summary>
/// The NETSCAPE-Bookmark-file-1 format every browser exports (Chrome, Edge, Firefox, Safari, Dia, Arc). It isn't well-formed HTML (&lt;DT&gt; and &lt;p&gt; are
/// never closed), so it's tokenised rather than handed to an HTML parser. The C# twin of src/shared/import/netscapeBookmarks.ts, held to it by
/// parsers.vectors.json. The bar is the folder marked PERSONAL_TOOLBAR_FOLDER; a source that marks none (Safari) has an empty bar.
/// </summary>
public static partial class NetscapeBookmarks
{
	// ECMAScript keeps \s, \d and \b ASCII-only, as in the JavaScript original.
	private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.ECMAScript;

	// A tag's attributes never hold a raw "<", so an unclosed tag fails at the next one instead of scanning the rest of the file.
	[GeneratedRegex(@"<(\/?)(dl|h3|a)\b([^<>]*)>|<\/(h3|a)>", Options, matchTimeoutMilliseconds: 2000)]
	private static partial Regex Token();

	[GeneratedRegex(@"<!DOCTYPE\s+NETSCAPE-Bookmark-file-1>", Options)]
	private static partial Regex Doctype();

	[GeneratedRegex("<DL>", Options)]
	private static partial Regex ListStart();

	[GeneratedRegex(@"PERSONAL_TOOLBAR_FOLDER\s*=\s*""true""", Options)]
	private static partial Regex ToolbarMark();

	[GeneratedRegex(@"HREF\s*=\s*""([^""]*)""", Options)]
	private static partial Regex Href();

	[GeneratedRegex(@"&(#x[0-9a-f]+|#\d+|[a-z]+);", Options)]
	private static partial Regex Entity();

	/// <exception cref="NetscapeBookmarksFormatException">No NETSCAPE-Bookmark-file-1 doctype and no &lt;DL&gt;.</exception>
	public static BookmarkImport Parse(string html)
	{
		if (!Doctype().IsMatch(html) && !ListStart().IsMatch(html))
			throw new NetscapeBookmarksFormatException("Not a bookmarks HTML export (no NETSCAPE-Bookmark-file-1 doctype or <DL>)");
		List<ImportedNode> root = [];
		List<List<ImportedNode>> stack = [];
		List<string> skipped = [];
		(string Title, bool IsBar)? pendingFolder = null;
		List<ImportedNode>? bar = null;
		for (var match = Token().Match(html); match.Success; match = match.NextMatch())
		{
			var closing = match.Groups[1].Value;
			var name = match.Groups[2].Value.ToLowerInvariant();
			var attrs = match.Groups[3].Value;
			var after = match.Index + match.Length;
			if (name == "h3" && closing == "")
			{
				pendingFolder = (DecodeEntities(TextUntilTag(html, after)), ToolbarMark().IsMatch(attrs));
				continue;
			}
			if (name == "dl" && closing == "")
			{
				if (stack.Count == 0 && pendingFolder is null)
				{
					stack.Add(root);
					continue;
				}
				var parent = stack.Count > 0 ? stack[^1] : root;
				List<ImportedNode> children = [];
				if (pendingFolder?.IsBar ?? false) bar = children;
				else parent.Add(new ImportedFolder(pendingFolder?.Title ?? "", children));
				stack.Add(children);
				pendingFolder = null;
				continue;
			}
			if (name == "dl")
			{
				if (stack.Count > 0) stack.RemoveAt(stack.Count - 1);
				continue;
			}
			if (name == "a" && closing == "")
			{
				var title = DecodeEntities(TextUntilTag(html, after));
				var url = DecodeEntities(Href().Match(attrs) is { Success: true } href ? href.Groups[1].Value : "");
				if (!BookmarkImport.AcceptsBookmarkUrl(url))
				{
					skipped.Add($"{(title == "" ? url : title)}: {BookmarkImport.NotHttpReason}");
					continue;
				}
				(stack.Count > 0 ? stack[^1] : root).Add(new ImportedLink(title == "" ? url : title, url));
			}
		}
		return new BookmarkImport(bar ?? [], UnwrapSingleRoot(root), skipped);
	}

	// A title ends at the next tag, closing or not: exports write a "<" in a title as &lt;, and some never close their <A> and <H3> tags.
	private static string TextUntilTag(string html, int start)
	{
		var end = html.IndexOf('<', start);
		return (end == -1 ? html[start..] : html[start..end]).Trim();
	}

	// Firefox and Safari wrap everything in one top-level folder; keep its contents rather than an extra empty level.
	private static List<ImportedNode> UnwrapSingleRoot(List<ImportedNode> nodes) =>
		nodes is [ImportedFolder { Title: "" } only] ? [.. only.Children] : nodes;

	// Most titles and addresses hold no entity; skipping the regex for them is most of the time a big export takes.
	public static string DecodeEntities(string text) => !text.Contains('&') ? text : Entity().Replace(text, match =>
	{
		var body = match.Groups[1].Value;
		if (body[0] == '#')
		{
			var hex = body[1] is 'x' or 'X';
			if (!int.TryParse(body.AsSpan(hex ? 2 : 1), hex ? NumberStyles.AllowHexSpecifier : NumberStyles.None, CultureInfo.InvariantCulture, out var code) || code > 0x10FFFF) return match.Value;
			return code < 0x10000 ? ((char)code).ToString() : char.ConvertFromUtf32(code);
		}
		return body.ToLowerInvariant() switch
		{
			"amp" => "&",
			"lt" => "<",
			"gt" => ">",
			"quot" => "\"",
			"apos" => "'",
			"nbsp" => " ",
			_ => match.Value,
		};
	});
}
