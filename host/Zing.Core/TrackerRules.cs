namespace Zing.Core;

/// <summary>
/// src/shared/browser/trackerRules.ts: the domains blockTrackers refuses as third-party requests (Krackn's TrackerRules).
/// The list is the TypeScript one, exported to Defaults/tracker-domains.json.
/// </summary>
public static class TrackerRules
{
	public static IReadOnlyList<string> Domains { get; } = [.. ZingJson.LoadDefaults("tracker-domains.json").AsArray().Select(d => d!.GetValue<string>())];

	/// <summary>The host is a listed domain or under one.</summary>
	public static bool IsTrackerHost(string rawHost)
	{
		var host = rawHost.ToLowerInvariant();
		if (host.EndsWith('.')) host = host[..^1];
		return Domains.Any(domain => host == domain || host.EndsWith("." + domain, StringComparison.Ordinal));
	}

	/// <summary>An http(s) request to a listed domain from a page on another site; a page with no web address counts as another site.</summary>
	public static bool Blocks(string requestUrl, string pageUrl)
	{
		if (WebHostOf(requestUrl) is not { } request || !IsTrackerHost(request)) return false;
		if (WebHostOf(pageUrl) is not { } page) return true;
		return RegistrableDomain.Of(request) != RegistrableDomain.Of(page);
	}

	private static string? WebHostOf(string url)
	{
		if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)) return null;
		return parsed.Scheme is "http" or "https" ? parsed.IdnHost : null;
	}
}
