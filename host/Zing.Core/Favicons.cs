using System.Text.Json;

namespace Zing.Core;

/// <param name="Rel">Lowercased: "icon", "shortcut icon", "apple-touch-icon"...</param>
/// <param name="Sizes">Lowercased, e.g. "32x32", "16x16 32x32" or "any".</param>
/// <param name="MediaMatches">Whether the link's `media` query (if any) matches the page right now.</param>
public sealed record FaviconLink(string Href, string Rel, string Sizes, string Type, bool MediaMatches)
{
	public static FaviconLink Make(string href, string rel, string? sizes = null, string? type = null, bool? mediaMatches = null) =>
		new(href, rel.ToLowerInvariant(), (sizes ?? "").ToLowerInvariant(), (type ?? "").ToLowerInvariant(), mediaMatches ?? true);
}

/// <summary>Port of src/shared/browser/faviconChoice.ts: which of a page's declared icons to show.</summary>
public static class FaviconChoice
{
	/// <summary>
	/// Run in the page to read the icons it declares as `{ href, rel, sizes, type, mediaMatches }`. The
	/// `media` query is evaluated there, against the page's own colour scheme: GitHub, among others,
	/// declares a white icon for dark mode, and taking the wrong one leaves an invisible icon.
	/// </summary>
	public const string ProbeScript =
		"Array.from(document.querySelectorAll(\"link[rel~='icon' i], link[rel='apple-touch-icon' i], link[rel='apple-touch-icon-precomposed' i]\"))"
		+ ".slice(0, 32).map(l => ({ href: l.href || '', rel: l.rel || '', sizes: l.getAttribute('sizes') || '', type: l.type || '', mediaMatches: !l.media || matchMedia(l.media).matches }))";

	/// <summary>Reads the probe script's result. Entries without a string href are skipped, as in the TS.</summary>
	public static IReadOnlyList<FaviconLink> ParseProbe(JsonElement probe)
	{
		if (probe.ValueKind != JsonValueKind.Array) return [];
		var links = new List<FaviconLink>();
		foreach (var item in probe.EnumerateArray())
		{
			if (item.ValueKind != JsonValueKind.Object) continue;
			if (!item.TryGetProperty("href", out var href) || href.ValueKind != JsonValueKind.String) continue;
			links.Add(FaviconLink.Make(
				href.GetString()!,
				Text(item, "rel") ?? "",
				Text(item, "sizes"),
				Text(item, "type"),
				item.TryGetProperty("mediaMatches", out var media) && media.ValueKind is JsonValueKind.True or JsonValueKind.False ? media.GetBoolean() : null));
		}
		return links;
	}

	private static string? Text(JsonElement item, string name) =>
		item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

	/// <summary>"16x16 32x32" gives 32; null when no token has a width.</summary>
	public static int? LargestSize(string sizes)
	{
		int? largest = null;
		foreach (var token in sizes.Split(' ', StringSplitOptions.RemoveEmptyEntries))
		{
			var parts = token.Split('x', StringSplitOptions.RemoveEmptyEntries);
			if (parts.Length != 2 || !IsInteger(parts[0]) || !int.TryParse(parts[0], out var width)) continue;
			if (largest is null || width > largest) largest = width;
		}
		return largest;
	}

	private static bool IsInteger(string text)
	{
		var digits = text.Length > 0 && text[0] is '+' or '-' ? text[1..] : text;
		return digits.Length > 0 && digits.All(char.IsAsciiDigit);
	}

	private static bool IsUsable(FaviconLink link)
	{
		if (link.Href == "" || !link.MediaMatches) return false;
		return link.Href.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
			|| link.Href.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
			|| link.Href.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase);
	}

	private static int Score(FaviconLink link)
	{
		var score = 0;
		var isTouch = link.Rel.Contains("apple-touch", StringComparison.Ordinal);
		if (!isTouch && link.Rel.Contains("icon", StringComparison.Ordinal)) score += 1000;
		var px = LargestSize(link.Sizes);
		if (link.Sizes == "any" || link.Type.Contains("svg", StringComparison.Ordinal)) score += 300;
		else if (px is { } width)
			// Closest to 32 wins; bigger than needed beats too small.
			score += width >= 32 ? 400 - Math.Min(width - 32, 300) : 200 - (32 - width) * 4;
		else score += 250;
		return score;
	}

	/// <summary>
	/// Tab icons draw at 14-16px, so 32px (2x) is ideal. Prefers a real `icon` over an apple-touch-icon,
	/// the size nearest 32 among those, and an SVG or "any" size when nothing is labelled, then the page's order.
	/// </summary>
	public static string? Best(IEnumerable<FaviconLink> links)
	{
		FaviconLink? chosen = null;
		var chosenScore = int.MinValue;
		foreach (var link in links.Where(IsUsable))
		{
			var score = Score(link);
			if (score <= chosenScore) continue;
			chosen = link;
			chosenScore = score;
		}
		return chosen?.Href;
	}
}

public sealed class IconRejectedException(string url, string detail) : Exception($"not using {url} as an icon: {detail}")
{
	public string Url { get; } = url;
}

/// <summary>The limits src/main/favicon-service.ts puts on a fetched icon. Fetching and resizing to <see cref="IconPixels"/> are the host's.</summary>
public static class IconLimits
{
	public const int MinBytes = 60;
	public const int MaxBytes = 2_000_000;
	public const int MaxDataUrlChars = 200_000;
	public const int IconPixels = 32;
	public static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(6);
	public static readonly TimeSpan ProbeDebounce = TimeSpan.FromMilliseconds(250);

	public static bool IsFetchable(string candidate) =>
		candidate.StartsWith("http:", StringComparison.OrdinalIgnoreCase)
		|| candidate.StartsWith("https:", StringComparison.OrdinalIgnoreCase)
		|| candidate.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase);

	/// <summary>A "not found" page served as HTML is not an icon: throws <see cref="IconRejectedException"/> unless the body is plausible.</summary>
	public static void CheckDownloaded(string url, int byteCount)
	{
		if (byteCount < MinBytes || byteCount > MaxBytes) throw new IconRejectedException(url, $"{byteCount} bytes");
	}

	public static void CheckDataUrl(string url)
	{
		if (url.Length > MaxDataUrlChars) throw new IconRejectedException("(data URL)", "too large");
	}

	/// <summary>The first candidate <paramref name="download"/> accepts. A dead or invalid one (<see cref="IconRejectedException"/>, <see cref="HttpRequestException"/>) means try the next.</summary>
	public static async Task<string?> FirstLoadedAsync(IReadOnlyList<string> candidates, Func<string, Task<string>> download)
	{
		foreach (var candidate in candidates)
		{
			if (!IsFetchable(candidate)) continue;
			try
			{
				return await download(candidate);
			}
			catch (Exception error) when (error is IconRejectedException or HttpRequestException)
			{
			}
		}
		return null;
	}
}

/// <summary>Resolves to a data URL for the first candidate that is a usable image, or null when none is.</summary>
public delegate Task<string?> FetchIcon(IReadOnlyList<string> candidates);

/// <summary>
/// Port of src/main/favicons.ts. Memory-only site icons: a favicon cache on disk is a dated list of every
/// site visited, so this type never writes anything. Keyed by the icon's own address when the page
/// declares one, and by the full host otherwise, never by registrable domain. A null value records
/// "looked, there isn't one" so a miss isn't fetched again. Least recently used entries go first at capacity.
/// </summary>
public sealed class FaviconStore(FetchIcon fetchIcon, Action onChange, int capacity = 256)
{
	private readonly OrderedDictionary<string, string?> _entries = [];
	private readonly HashSet<string> _inFlight = [];

	public int Size => _entries.Count;

	/// <summary>The conventional places for a site that declares no icon.</summary>
	public static IReadOnlyList<string> ConventionalIconUrls(string host) =>
		[$"https://{host}/favicon.ico", $"https://{host}/apple-touch-icon.png", $"https://{host}/favicon.png"];

	/// <summary>The page's own icon if it declared one and it has loaded, otherwise the host's conventional one.</summary>
	public string? Icon(string? iconUrl, string host) =>
		(string.IsNullOrEmpty(iconUrl) ? null : Recall(IconKey(iconUrl))) ?? (host == "" ? null : Recall(HostKey(host)));

	public Task LoadIcon(string iconUrl) => LoadOnce(IconKey(iconUrl), [iconUrl]);

	public Task LoadHost(string host) => host == "" ? Task.CompletedTask : LoadOnce(HostKey(host), ConventionalIconUrls(host));

	private static string IconKey(string iconUrl) => $"icon:{iconUrl}";

	private static string HostKey(string host) => $"host:{host.ToLowerInvariant()}";

	private string? Recall(string key)
	{
		if (!_entries.Remove(key, out var value)) return null;
		// Re-insert so the entry counts as recently used.
		_entries.Add(key, value);
		return value;
	}

	private async Task LoadOnce(string key, IReadOnlyList<string> candidates)
	{
		if (_entries.ContainsKey(key) || !_inFlight.Add(key)) return;
		try
		{
			Remember(key, await fetchIcon(candidates));
		}
		finally
		{
			_inFlight.Remove(key);
		}
		onChange();
	}

	private void Remember(string key, string? value)
	{
		_entries[key] = value;
		while (_entries.Count > capacity) _entries.RemoveAt(0);
	}
}

/// <summary>Port of the decisions in src/main/favicon-service.ts: which icon a tab shows and what to load for a page.</summary>
public sealed class FaviconService(FaviconStore store)
{
	public FaviconStore Store => store;

	public string? IconFor(string? faviconUrl, string pageUrl) => store.Icon(faviconUrl, HostOf(pageUrl));

	/// <summary>The http(s) host of a page, or "" for anything else (about:, file:, the start page...).</summary>
	public static string HostOf(string url) =>
		Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri.Host : "";

	/// <summary>The engine reports an icon address for the page (WebView2 `FaviconChanged`, Electron `page-favicon-updated`).</summary>
	public Task OnDeclared(string iconUrl) => store.LoadIcon(iconUrl);

	/// <summary>
	/// After the probe script ran on the loaded page. <paramref name="setFaviconUrl"/> is null when the
	/// page has no tab to record it on. With no declared icon, the host's conventional one is loaded.
	/// </summary>
	public Task OnProbed(string pageUrl, JsonElement probeResult, Action<string>? setFaviconUrl)
	{
		var host = HostOf(pageUrl);
		if (host == "") return Task.CompletedTask;
		var chosen = FaviconChoice.Best(FaviconChoice.ParseProbe(probeResult));
		if (chosen is null || setFaviconUrl is null) return store.LoadHost(host);
		setFaviconUrl(chosen);
		return store.LoadIcon(chosen);
	}
}
