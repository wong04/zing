using Zing.Core.Net;

namespace Zing.Core;

/// <summary>
/// What Zing says when a page takes the whole screen. The page then covers the chrome and could draw a fake address bar,
/// so the notice names the site that has the screen (a relayed page by the site it stands for) and how to leave.
/// </summary>
public static class FullScreenHint
{
	public static string Text(string url) => $"{SiteOf(url)} is now full screen · Press Esc to exit";

	private static string SiteOf(string url)
	{
		var web = RelayedContent.ToWebAddress(url);
		if (!Uri.TryCreate(web, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return "This page";
		return uri.Authority;
	}
}
