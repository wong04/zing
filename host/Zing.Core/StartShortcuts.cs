using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Zing.Core;

/// <summary>One tile on the new-tab page. Its identity is its URL.</summary>
public sealed record StartShortcut(string Title, string Url);

/// <summary>Port of src/shared/browser/startShortcuts.ts. Storage is the caller's: keep the encoded string, and store nothing to restore the defaults.</summary>
public static class StartShortcuts
{
	/// <summary>Enough to fill four rows of the page's grid without it turning into a bookmarks list.</summary>
	public const int Limit = 16;

	public static readonly IReadOnlyList<StartShortcut> Defaults =
	[
		new("GitHub", "https://github.com"),
		new("Stack Overflow", "https://stackoverflow.com"),
		new("arXiv", "https://arxiv.org"),
		new("MDN", "https://developer.mozilla.org"),
		new("Wikipedia", "https://wikipedia.org"),
		new("Wolfram Alpha", "https://www.wolframalpha.com"),
		new("Desmos", "https://www.desmos.com/calculator"),
		new("Hacker News", "https://news.ycombinator.com"),
	];

	private static readonly Dictionary<string, string> Glyphs = new(StringComparer.Ordinal)
	{
		["https://github.com"] = "◱",
		["https://stackoverflow.com"] = "◲",
		["https://arxiv.org"] = "✦",
		["https://developer.mozilla.org"] = "◳",
		["https://wikipedia.org"] = "◈",
		["https://www.wolframalpha.com"] = "∑",
		["https://www.desmos.com/calculator"] = "∿",
		["https://news.ycombinator.com"] = "▲",
	};

	/// <summary>User-perceived characters, as JS Intl.Segmenter counts them.</summary>
	private static List<string> Graphemes(string text)
	{
		var result = new List<string>();
		var parts = StringInfo.GetTextElementEnumerator(text);
		while (parts.MoveNext()) result.Add((string)parts.Current);
		return result;
	}

	private static string UpperFirst(string grapheme) => grapheme.ToUpperInvariant();

	public static string Glyph(StartShortcut shortcut)
	{
		if (Glyphs.TryGetValue(shortcut.Url, out var builtIn)) return builtIn;
		var first = Graphemes(AddressInput.JsTrim(shortcut.Title)).FirstOrDefault();
		return first is null ? "•" : UpperFirst(first);
	}

	private static List<StartShortcut>? ParseShortcutList(string raw)
	{
		JsonElement parsed;
		try {
			using var document = JsonDocument.Parse(raw);
			parsed = document.RootElement.Clone();
		} catch (JsonException) {
			return null;
		}
		if (parsed.ValueKind != JsonValueKind.Array) return null;
		var list = new List<StartShortcut>();
		foreach (var item in parsed.EnumerateArray()) {
			if (item.ValueKind != JsonValueKind.Object) return null;
			if (!item.TryGetProperty("title", out var title) || title.ValueKind != JsonValueKind.String) return null;
			if (!item.TryGetProperty("url", out var url) || url.ValueKind != JsonValueKind.String) return null;
			list.Add(new StartShortcut(title.GetString()!, url.GetString()!));
		}
		return list;
	}

	/// <summary>
	/// Nothing stored, or something unreadable, gives the built-in set. Entries that
	/// aren't web addresses are dropped: the list ends up in a page's hrefs.
	/// </summary>
	public static List<StartShortcut> Decode(string? raw)
	{
		var list = raw is null ? null : ParseShortcutList(raw);
		if (list is null) return [.. Defaults];
		return [.. list.Where(s => IsWebAddress(s.Url)).Take(Limit)];
	}

	public static string Encode(IEnumerable<StartShortcut> list)
	{
		var builder = new StringBuilder("[");
		var first = true;
		foreach (var shortcut in list.Take(Limit)) {
			if (!first) builder.Append(',');
			first = false;
			builder.Append("{\"title\":");
			AppendJsString(builder, shortcut.Title);
			builder.Append(",\"url\":");
			AppendJsString(builder, shortcut.Url);
			builder.Append('}');
		}
		return builder.Append(']').ToString();
	}

	/// <summary>JSON.stringify of a string.</summary>
	public static string JsString(string text)
	{
		var builder = new StringBuilder();
		AppendJsString(builder, text);
		return builder.ToString();
	}

	/// <summary>JSON.stringify's string form: only quotes, backslashes, controls and lone surrogates are escaped.</summary>
	private static void AppendJsString(StringBuilder builder, string text)
	{
		builder.Append('"');
		for (var i = 0; i < text.Length; i++) {
			var c = text[i];
			switch (c) {
				case '"': builder.Append("\\\""); break;
				case '\\': builder.Append("\\\\"); break;
				case '\b': builder.Append("\\b"); break;
				case '\f': builder.Append("\\f"); break;
				case '\n': builder.Append("\\n"); break;
				case '\r': builder.Append("\\r"); break;
				case '\t': builder.Append("\\t"); break;
				default:
					if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) {
						builder.Append(c).Append(text[++i]);
					} else if (c < 0x20 || char.IsSurrogate(c)) {
						builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
					} else {
						builder.Append(c);
					}
					break;
			}
		}
		builder.Append('"');
	}

	/// <summary>The stored setting (null or an array of {title, url}) as the list the page shows.</summary>
	public static List<StartShortcut> FromSetting(JsonNode? stored)
	{
		if (stored is null) return Decode(null);
		var list = stored.AsArray().Select(item => new StartShortcut(item!["title"]!.GetValue<string>(), item["url"]!.GetValue<string>()));
		return Decode(Encode(list));
	}

	/// <summary>A shortcut from what was typed: the address by the address bar's rule, the name if given, else one made from the site. Null if it isn't a web address.</summary>
	public static StartShortcut? Make(string rawTitle, string address)
	{
		if (AddressInput.Classify(address) is not AddressInputResult.Url result || !IsWebAddress(result.Value)) return null;
		var title = AddressInput.JsTrim(rawTitle);
		return new StartShortcut(title == "" ? Name(result.Value) : string.Concat(Graphemes(title).Take(40)), result.Value);
	}

	/// <summary>The host as written: a parsed URL would lowercase it and punycode `bücher.de`, which is no name for a tile.</summary>
	private static string HostAsWritten(string url)
	{
		// indexOf is -1 without a scheme, so JS slices from 2 there; kept as is.
		var afterScheme = url[Math.Min(url.IndexOf("://", StringComparison.Ordinal) + 3, url.Length)..];
		var authority = afterScheme.Split('/', '?', '#')[0];
		var hostAndPort = authority[(authority.LastIndexOf('@') + 1)..];
		var colon = hostAndPort.LastIndexOf(':');
		if (colon >= 0 && hostAndPort[(colon + 1)..].All(char.IsAsciiDigit)) return hostAndPort[..colon];
		return hostAndPort;
	}

	/// <summary>`www.apple.com` gives "Apple", `docs.google.com` gives "Docs".</summary>
	public static string Name(string url)
	{
		var host = HostAsWritten(url);
		if (host == "") host = url;
		if (host.StartsWith("www.", StringComparison.Ordinal)) host = host[4..];
		var first = host.Split('.').FirstOrDefault(label => label.Length > 0) ?? host;
		var parts = Graphemes(first);
		if (parts.Count == 0) return "";
		return UpperFirst(parts[0]) + string.Concat(parts.Skip(1));
	}

	public static bool IsWebAddress(string text) => WebUri(text) is not null;

	/// <summary>The parsed http(s) address with a host, or null. Stands in for `URL.canParse` plus a protocol and hostname check.</summary>
	public static Uri? WebUri(string text)
	{
		if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return null;
		if (uri.Scheme is not ("http" or "https")) return null;
		return uri.IdnHost == "" ? null : uri;
	}

	/// <summary>JS `URL.hostname`: punycode, lower case, IPv6 in brackets.</summary>
	public static string Hostname(Uri uri) => uri.HostNameType == UriHostNameType.IPv6 ? uri.Host : uri.IdnHost;

	/// <summary>For putting your names and addresses into the page's HTML.</summary>
	public static string EscapeHtml(string text)
	{
		var builder = new StringBuilder(text.Length);
		foreach (var c in text) {
			switch (c) {
				case '&': builder.Append("&amp;"); break;
				case '<': builder.Append("&lt;"); break;
				case '>': builder.Append("&gt;"); break;
				case '"': builder.Append("&quot;"); break;
				case '\'': builder.Append("&#39;"); break;
				default: builder.Append(c); break;
			}
		}
		return builder.ToString();
	}
}
