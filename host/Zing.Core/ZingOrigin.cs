namespace Zing.Core;

/// <summary>Where Zing's own pages are served from: the only origin whose messages the host answers.</summary>
public static class ZingOrigin
{
	public const string Host = "app.zing";

	public static bool IsZingView(string source) => source.StartsWith($"https://{Host}/", StringComparison.Ordinal);

	/// <summary>
	/// Whether a renderer view (Zing's own UI) may fetch this address as a document at all: https on app.zing or view.zing, on
	/// the default port, with no sign-in. The address is parsed and its origin compared, never its text.
	/// </summary>
	public static bool IsRendererDocument(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && IsZingOrigin(uri) && uri.Host is Host or Viewer.ViewerFiles.Host;

	/// <summary>Whether a frame inside a renderer view may load this address: only the viewer's files (view.zing), parsed as an origin.</summary>
	public static bool IsViewerDocument(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && IsZingOrigin(uri) && uri.Host == Viewer.ViewerFiles.Host;

	/// <summary>
	/// Whether a renderer view's top document may become <paramref name="url"/>: only its own page (<paramref name="home"/>,
	/// the address Zing opened it at), when Zing first opens it or reloads it after a crash. Anything else, a link in a PDF to
	/// view.zing or to app.zing included, would replace or reload Zing's own UI.
	/// </summary>
	public static bool MayLoadAsView(string home, string url, bool first, bool reload) => url == home && (first || reload);

	private static bool IsZingOrigin(Uri uri) => uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && uri.UserInfo.Length == 0;
}
