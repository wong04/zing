namespace Zing.Core;

/// <summary>Where Zing's own pages are served from: the only origin whose messages the host answers.</summary>
public static class ZingOrigin
{
	public const string Host = "app.zing";

	/// <summary>
	/// A name no network knows: Zing's own views, and only they, have it answered by the host (LocalFiles). It is not the
	/// mapped app folder's host, because WebView2 answers a mapped host from its folder without raising the host's request event.
	/// </summary>
	public const string LocalFilesHost = "local.zing";

	public static bool IsZingView(string source) => source.StartsWith($"https://{Host}/", StringComparison.Ordinal);
}
