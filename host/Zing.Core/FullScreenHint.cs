using Zing.Core.Net;

namespace Zing.Core;

/// <summary>
/// What Zing says when a page takes the whole screen. The page then covers the chrome and could draw a fake address bar,
/// so the notice names the site that has the screen (a relayed page by the site it stands for) and how to leave.
/// </summary>
public static class FullScreenHint
{
	/// <summary>The window went fullscreen by F11: no site took it, so the hint only says how to leave.</summary>
	public const string Browser = "Full screen · Press F11 or Esc to exit";

	public static string Text(string url) => $"{SiteOf(url)} is now full screen · Press Esc to exit";

	private static string SiteOf(string url)
	{
		var web = RelayedContent.ToWebAddress(url);
		if (!Uri.TryCreate(web, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return "This page";
		return uri.Authority;
	}
}
