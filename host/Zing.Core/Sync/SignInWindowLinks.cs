using Zing.Core.Net;

namespace Zing.Core.Sync;

/// <summary>
/// Which window requests from a page in the Google sign-in window (Zing.Host.SignInPopup) open a tab: only a link the person
/// clicked, to an http or https address, that tabs would open too (RelayedNavigation.NewWindowTarget, ThreatGate.NewWindowAllowed).
/// Anything else opens nothing: script's window.open without a click, Zing's own schemes, the relay.
/// </summary>
public static class SignInWindowLinks
{
	/// <summary>The address to open as a tab, or null to open nothing.</summary>
	/// <param name="opener">The sign-in window's address.</param>
	public static string? TabFor(string opener, string target, bool userInitiated)
	{
		if (!userInitiated || !IsWeb(target)) return null;
		if (RelayedNavigation.NewWindowTarget(opener, target) is not { } tab || !IsWeb(tab)) return null;
		return ThreatGate.NewWindowAllowed(tab) ? tab : null;
	}

	private static bool IsWeb(string address) =>
		Uri.TryCreate(address, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);
}
