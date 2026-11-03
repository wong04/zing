namespace Zing.Core.Net;

/// <summary>Which load failures look like a network block rather than a site's own trouble.</summary>
public static class BlockLike
{
	/// <summary>The engine's web error statuses a filter causes: no address, no connection, a reset, a bad certificate, or no answer. HTTP error statuses are not among them.</summary>
	private static readonly HashSet<string> Statuses = new(StringComparer.Ordinal)
	{
		"HostNameNotResolved",
		"CannotConnect",
		"ServerUnreachable",
		"ConnectionReset",
		"Timeout",
		"CertificateCommonNameIsIncorrect",
		"CertificateExpired",
		"CertificateRevoked",
		"CertificateIsInvalid",
	};

	public static bool Is(string webErrorStatus) => Statuses.Contains(webErrorStatus);
}

/// <summary>When a failed load is worth offering Open a Link and Diagnose for.</summary>
public static class LoadFailureOffer
{
	/// <summary>
	/// The address to offer them for, or null for no offer: only an ordinary web page that failed like a block gets one, so not
	/// Zing's own pages, a relayed page (the relay already is the workaround), a stopped load or an HTTP error.
	/// </summary>
	public static string? For(string url, string webErrorStatus)
	{
		if (!BlockLike.Is(webErrorStatus)) return null;
		if (!Uri.TryCreate(url, UriKind.Absolute, out var address)) return null;
		if (address.Scheme != Uri.UriSchemeHttp && address.Scheme != Uri.UriSchemeHttps) return null;
		// Zing's own renderer is served from a virtual https host.
		if (address.Host == "app.zing") return null;
		return address.AbsoluteUri;
	}

	/// <summary>The address as the offer shows it: without a user name and password, which have no place on screen.</summary>
	public static string Display(string address)
	{
		var uri = new Uri(address);
		return uri.UserInfo == "" ? uri.AbsoluteUri : uri.GetComponents(UriComponents.AbsoluteUri & ~UriComponents.UserInfo, UriFormat.SafeUnescaped);
	}
}

/// <summary>What <c>relay:open</c> answers: which way worked, for which address, and every attempt. Not the page, which is untrusted text the tool never shows.</summary>
public sealed record OpenLinkResult(RelayOutcome Outcome, RelayStrategy? Strategy, string Address, IReadOnlyList<RelayAttempt> Attempts);

/// <summary>A button of an offer that is no longer on screen was pressed.</summary>
public sealed class NoOfferError() : NetError("There is no offer to act on.");

public sealed class UnknownOfferToolError(string tool) : NetError($"The offer has no tool called {tool}.");
