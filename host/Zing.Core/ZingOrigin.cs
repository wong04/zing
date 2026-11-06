namespace Zing.Core;

/// <summary>Where Zing's own pages are served from: the only origin whose messages the host answers.</summary>
public static class ZingOrigin
{
	public const string Host = "app.zing";

	public static bool IsZingView(string source) => source.StartsWith($"https://{Host}/", StringComparison.Ordinal);

	/// <summary>
	/// Whether a renderer view (Zing's own UI) may load this address, in its top frame or any other: https on app.zing or
	/// view.zing, on the default port, with no sign-in. The address is parsed and its origin compared, never its text.
	/// </summary>
	public static bool IsRendererDocument(string url) =>
		Uri.TryCreate(url, UriKind.Absolute, out var uri)
		&& uri.Scheme == Uri.UriSchemeHttps
		&& uri.IsDefaultPort
		&& uri.UserInfo.Length == 0
		&& uri.Host is Host or Viewer.ViewerFiles.Host;
}
