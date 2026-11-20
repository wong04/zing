using System.Globalization;
using System.Text;

namespace Zing.Core.Import;

/// <summary>Where the export went (its name only, never the path) and how many bookmarks it holds.</summary>
public sealed record BookmarkExportOutcome(string Name, int Bookmarks);

/// <summary>
/// Writes the library's bookmarks as a NETSCAPE-Bookmark-file-1 file, the format every browser imports and <see cref="NetscapeBookmarks"/> reads.
/// Favourites is the folder marked PERSONAL_TOOLBAR_FOLDER, so it lands on the other browser's bar; Pinned and every other folder follow in
/// library order. The Reading List isn't bookmarks and stays out.
/// </summary>
public static class BookmarkExport
{
	public const string SuggestedFileName = "Zing bookmarks.html";

	public static string Write(BrowserLibrary library)
	{
		var html = new StringBuilder();
		html.Append("""
			<!DOCTYPE NETSCAPE-Bookmark-file-1>
			<!-- This is an automatically generated file.
			     It will be read and overwritten.
			     DO NOT EDIT! -->
			<META HTTP-EQUIV="Content-Type" CONTENT="text/html; charset=UTF-8">
			<TITLE>Bookmarks</TITLE>
			<H1>Bookmarks</H1>
			<DL><p>

			""");
		foreach (var node in library.Bookmarks) WriteNode(html, node, 1);
		html.Append("</DL><p>\n");
		return html.ToString();
	}

	public static int CountLinks(BrowserLibrary library) => library.Bookmarks.Sum(node => Library.Urls(node).Count);

	private static void WriteNode(StringBuilder html, BookmarkNode node, int depth)
	{
		var indent = new string(' ', depth * 4);
		var added = Seconds(node.UpdatedAt);
		if (node is BookmarkLink link)
		{
			html.Append($"{indent}<DT><A HREF=\"{Escape(link.Url)}\" ADD_DATE=\"{added}\">{Escape(link.Title)}</A>\n");
			return;
		}
		var folder = (BookmarkFolder)node;
		var bar = folder.Id == Library.FavouritesId ? " PERSONAL_TOOLBAR_FOLDER=\"true\"" : "";
		html.Append($"{indent}<DT><H3 ADD_DATE=\"{added}\" LAST_MODIFIED=\"{added}\"{bar}>{Escape(folder.Title)}</H3>\n");
		html.Append($"{indent}<DL><p>\n");
		foreach (var child in folder.Children) WriteNode(html, child, depth + 1);
		html.Append($"{indent}</DL><p>\n");
	}

	// The format counts seconds; the library counts milliseconds.
	private static string Seconds(long milliseconds) => (milliseconds / 1000).ToString(CultureInfo.InvariantCulture);

	// A title must never open a tag, nor an address end its attribute.
	private static string Escape(string text) => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
