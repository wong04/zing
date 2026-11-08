namespace Zing.Core.LinkPreview;

public enum LinkTrigger { Navigation, NewWindow }

public enum NavigationCause { AnchorClick, Other }

public readonly record struct Modifiers(bool Shift, bool Ctrl, bool Middle);

public sealed record PeekSettings(bool Enabled, bool ShiftClick, bool FromNewWindows);

public sealed record LinkRequest(string From, string To, LinkTrigger Trigger, NavigationCause Cause, Modifiers Modifiers, bool HasPopupFeatures, bool FromPreview);

public enum PeekVerdict { Normal, Preview, Undecided }

/// <summary>Whether a link opens as a preview (Arc's "Peek"). Pure: the tab code feeds it and acts on the verdict.</summary>
public static class PeekRules
{
	// Decision 4: sign-in hosts never preview, so the sign-in finishes in the tab that started it.
	private static readonly string[] SignInHosts = ["accounts.google.com", "login.microsoftonline.com", "login.live.com", "appleid.apple.com"];

	// Spec "never preview": a preview is easy to close by accident in a call.
	private static readonly string[] MeetingHosts = ["meet.google.com", "zoom.us", "teams.microsoft.com", "teams.live.com"];

	// Decision 10: results pages of these search engines never preview, matched by host plus results path.
	private static readonly SearchSource[] SearchSources =
	[
		new(IsGoogleHost, ["/search"], null),
		new(h => h == "duckduckgo.com", ["/"], "q"),
		new(h => h == "bing.com", ["/search"], null),
		new(h => h == "search.brave.com", ["/search"], null),
		new(h => h == "kagi.com", ["/search"], null),
		new(h => h == "startpage.com", ["/sp/search", "/do/search"], null),
	];

	private sealed record SearchSource(Func<string, bool> Host, string[] Paths, string? RequiredParam);

	public static PeekVerdict Decide(LinkRequest request, PeekSettings settings)
	{
		if (!settings.Enabled || request.FromPreview) return PeekVerdict.Normal;
		if (!TryHttp(request.From, out var from)) return PeekVerdict.Normal;
		if (request.Trigger == LinkTrigger.NewWindow && request.To == "about:blank") return PeekVerdict.Undecided;

		var to = Unwrap(request.To);
		if (!TryHttp(to, out var target)) return PeekVerdict.Normal;
		if (IsExcludedTarget(target)) return PeekVerdict.Normal;
		if (SameDocument(from, target)) return PeekVerdict.Normal;

		if (request.Trigger == LinkTrigger.NewWindow)
		{
			var m = request.Modifiers;
			if (m.Ctrl || m.Middle || request.HasPopupFeatures) return PeekVerdict.Normal;
			if (m.Shift) return settings.ShiftClick ? PeekVerdict.Preview : PeekVerdict.Normal;
			if (!settings.FromNewWindows) return PeekVerdict.Normal;
		}
		else if (request.Cause != NavigationCause.AnchorClick)
		{
			return PeekVerdict.Normal;
		}

		if (IsSearchResults(from)) return PeekVerdict.Normal;
		return SameHost(from, target) ? PeekVerdict.Normal : PeekVerdict.Preview;
	}

	/// <summary>google.com/url?q=… and ?url=… give their target; anything else is returned unchanged.</summary>
	public static string Unwrap(string url)
	{
		if (!TryHttp(url, out var uri)) return url;
		if (!IsGoogleHost(HostWithoutWww(uri)) || uri.AbsolutePath != "/url") return url;
		var target = QueryValue(uri, "q") ?? QueryValue(uri, "url");
		return target is not null && TryHttp(target, out _) ? target : url;
	}

	public static bool SameHost(string a, string b) =>
		Uri.TryCreate(a, UriKind.Absolute, out var x) && Uri.TryCreate(b, UriKind.Absolute, out var y) && SameHost(x, y);

	private static bool SameHost(Uri a, Uri b) => string.Equals(HostWithoutWww(a), HostWithoutWww(b), StringComparison.OrdinalIgnoreCase);

	// Zing's own pages (https://app.zing, and the zing: scheme) are not http pages that preview.
	private static bool TryHttp(string url, out Uri uri)
	{
		uri = null!;
		if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)) return false;
		if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) return false;
		if (parsed.Host == ZingOrigin.Host) return false;
		uri = parsed;
		return true;
	}

	private static string HostWithoutWww(Uri uri)
	{
		var host = uri.Host.ToLowerInvariant();
		return host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;
	}

	// google.<tld> or google.co.uk style, after www is dropped.
	private static bool IsGoogleHost(string host) => host.StartsWith("google.", StringComparison.Ordinal) && host.Length > "google.".Length;

	private static bool IsExcludedTarget(Uri uri)
	{
		var host = uri.Host.ToLowerInvariant();
		if (SignInHosts.Contains(host)) return true;
		if (host == "github.com" && IsGitHubSignInPath(uri.AbsolutePath)) return true;
		return MeetingHosts.Contains(host) || host.EndsWith(".zoom.us", StringComparison.Ordinal);
	}

	// Decision 4: GitHub's sign-in shares a host with ordinary link targets, so it is matched by path (/login and /login/oauth*).
	private static bool IsGitHubSignInPath(string path) => path == "/login" || path.StartsWith("/login/oauth", StringComparison.Ordinal);

	private static bool SameDocument(Uri a, Uri b) =>
		string.Equals(a.GetLeftPart(UriPartial.Query), b.GetLeftPart(UriPartial.Query), StringComparison.OrdinalIgnoreCase);

	private static bool IsSearchResults(Uri uri)
	{
		var host = HostWithoutWww(uri);
		return SearchSources.Any(s => s.Host(host)
			&& s.Paths.Contains(uri.AbsolutePath)
			&& (s.RequiredParam is null || QueryValue(uri, s.RequiredParam) is not null));
	}

	private static string? QueryValue(Uri uri, string name)
	{
		foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
		{
			var eq = pair.IndexOf('=');
			var key = eq < 0 ? pair : pair[..eq];
			if (key != name) continue;
			return eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
		}
		return null;
	}
}
