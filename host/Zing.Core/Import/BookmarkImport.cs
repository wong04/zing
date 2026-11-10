using System.Text.Json.Serialization;

namespace Zing.Core.Import;

/// <summary>A bookmark read from another browser. Serialises as the TypeScript shape, <c>{kind:'link',title,url}</c> or <c>{kind:'folder',title,children}</c>.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(ImportedLink), "link")]
[JsonDerivedType(typeof(ImportedFolder), "folder")]
public abstract record ImportedNode(string Title);

public sealed record ImportedLink(string Title, string Url) : ImportedNode(Title);

public sealed record ImportedFolder(string Title, IReadOnlyList<ImportedNode> Children) : ImportedNode(Title);

/// <summary>
/// What a parser found. <paramref name="Bar"/> is what the source showed on its bookmarks bar, <paramref name="Other"/> everything else as top-level
/// folders and links, <paramref name="Skipped"/> the entries that couldn't be imported, said out loud rather than dropped.
/// </summary>
public sealed record BookmarkImport(IReadOnlyList<ImportedNode> Bar, IReadOnlyList<ImportedNode> Other, IReadOnlyList<string> Skipped)
{
	internal const string NotHttpReason = "only http(s) addresses can be bookmarks";

	/// <summary>Only http(s) addresses become bookmarks: a javascript: bookmarklet would run in whatever page is open.</summary>
	public static bool AcceptsBookmarkUrl(string url) =>
		Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

	public static int CountLinks(IEnumerable<ImportedNode> nodes) =>
		nodes.Sum(node => node is ImportedFolder folder ? CountLinks(folder.Children) : 1);
}
