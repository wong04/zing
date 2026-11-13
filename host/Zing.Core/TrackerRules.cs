namespace Zing.Core;

/// <summary>
/// src/shared/browser/trackerRules.ts: the domains blockTrackers refuses as third-party requests (Krackn's TrackerRules).
/// The list is the TypeScript one, exported to Defaults/tracker-domains.json.
/// </summary>
public static class TrackerRules
{
	public static IReadOnlyList<string> Domains { get; } = [.. ZingJson.LoadDefaults("tracker-domains.json").AsArray().Select(d => d!.GetValue<string>())];

	private static readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> Listed = new HashSet<string>(Domains, StringComparer.OrdinalIgnoreCase).GetAlternateLookup<ReadOnlySpan<char>>();

	/// <summary>
	/// The host is a listed domain or under one. Every request a page makes is asked, on the UI thread, so it is a lookup per
	/// label of the host, without allocating, and never a walk of the list.
	/// </summary>
	public static bool IsTrackerHost(string rawHost)
	{
		var host = rawHost.AsSpan();
		if (host.EndsWith('.')) host = host[..^1];
		for (;;)
		{
			if (Listed.Contains(host)) return true;
			var dot = host.IndexOf('.');
			if (dot < 0) return false;
			host = host[(dot + 1)..];
		}
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
