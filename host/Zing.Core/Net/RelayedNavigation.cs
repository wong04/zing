namespace Zing.Core.Net;

/// <summary>Who started a main-frame navigation. Only Zing's own (typed address, tab wake-up, restore) may begin a relay load.</summary>
public enum NavigationOrigin
{
	/// <summary>A link, script, form or redirect of the page itself.</summary>
	Page,
	/// <summary>Zing called Navigate on the page.</summary>
	Zing,
	/// <summary>Reload. Only a document Zing allowed can be reloaded.</summary>
	Reload,
	/// <summary>Back or Forward. The history holds only navigations that were allowed; a page can't write a relay entry into it.</summary>
	History,
}

/// <param name="ChainIsRelay">The navigation this one is a redirect of began as an allowed relay load.</param>
public sealed record MainFrameNavigation(string From, string To, NavigationOrigin Origin, bool IsRedirected, bool ChainIsRelay);

/// <summary>
/// What may reach the relay scheme, and what a relayed page may reach. A relayed page is untrusted: it stays in the
/// relay, and nothing outside it can use Zing as a fetching proxy.
/// </summary>
public static class RelayedNavigation
{
	public static NavigationDecision MainFrame(MainFrameNavigation navigation)
	{
		var toRelay = RelayedContent.IsRelayUrl(navigation.To);
		if (navigation.IsRedirected) return Redirected(navigation, toRelay);
		if (navigation.Origin is not NavigationOrigin.Page) return NavigationDecision.Allow;
		var fromRelay = RelayedContent.IsRelayUrl(navigation.From);
		if (toRelay) return fromRelay ? NavigationDecision.Allow : NavigationDecision.Cancel;
		if (!fromRelay) return NavigationDecision.Allow;
		return StayInRelay(navigation.To);
	}

	/// <summary>A redirect onto the relay is Zing's own only inside a relay chain; one that leaves a relay chain can't come from Zing's responses, so it is refused.</summary>
	private static NavigationDecision Redirected(MainFrameNavigation navigation, bool toRelay)
	{
		if (toRelay) return navigation.ChainIsRelay ? NavigationDecision.Allow : NavigationDecision.Cancel;
		return navigation.ChainIsRelay ? NavigationDecision.Cancel : NavigationDecision.Allow;
	}

	/// <summary>An https link a relayed page builds at run time is pointed at the relay, as the rewriting does for the ones in its source; anything else is refused.</summary>
	private static NavigationDecision StayInRelay(string to)
	{
		if (Uri.TryCreate(to, UriKind.Absolute, out var web) && RelayedContent.Encode(web) is { } relay) return new NavigationDecision.Redirect(relay.AbsoluteUri);
		return NavigationDecision.Cancel;
	}

	/// <summary>A frame navigation (an iframe), which the main-frame event doesn't see.</summary>
	public static bool FrameAllowed(string top, string to)
	{
		var toRelay = RelayedContent.IsRelayUrl(to);
		var topRelay = RelayedContent.IsRelayUrl(top);
		if (toRelay) return topRelay;
		if (!topRelay) return true;
		return to.StartsWith("about:", StringComparison.OrdinalIgnoreCase);
	}

	public static bool NewWindowAllowed(string opener, string target)
	{
		var toRelay = RelayedContent.IsRelayUrl(target);
		var fromRelay = RelayedContent.IsRelayUrl(opener);
		if (toRelay) return fromRelay;
		return !fromRelay;
	}

	/// <summary>
	/// Whether the host may answer a request for a relay-scheme address. A subresource or frame of a relayed page may;
	/// a web page's image or fetch may not (it would make Zing fetch on a page's say), and the main document of a load
	/// is served only for the address that load was allowed for.
	/// </summary>
	public static bool MayServe(string top, string request, bool isDocument, string? approvedDocument)
	{
		if (RelayedContent.IsRelayUrl(top)) return true;
		return isDocument && approvedDocument is not null && Same(approvedDocument, request);
	}

	/// <summary>
	/// Whether a page's document request (main frame or frame) to the network may go out. Cancelling a navigation does not stop the engine
	/// sending its request first, so a relayed page that navigates to an address would still make Zing contact it: only the
	/// navigation Zing itself allowed (the address bar, Back) may.
	/// </summary>
	public static bool MayRequestDocument(string top, string request, string? approvedDocument)
	{
		if (!RelayedContent.IsRelayUrl(top)) return true;
		return approvedDocument is not null && Same(approvedDocument, request);
	}

	public static bool Same(string a, string b) =>
		Uri.TryCreate(a, UriKind.Absolute, out var x) && Uri.TryCreate(b, UriKind.Absolute, out var y) ? x.AbsoluteUri == y.AbsoluteUri : a == b;
}
