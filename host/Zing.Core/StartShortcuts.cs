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

	/// <summary>
	/// JS `toUpperCase`, which uses Unicode's full case mapping: `ß` becomes `SS`, `ﬁ` becomes `FI`.
	/// .NET maps one char to one char, so the characters whose capital is longer (SpecialCasing.txt) are looked up here.
	/// </summary>
	private static string UpperFirst(string grapheme)
	{
		var builder = new StringBuilder(grapheme.Length);
		var run = 0;
		for (var i = 0; i < grapheme.Length; i++) {
			if (!FullUpper.TryGetValue(grapheme[i], out var expanded)) continue;
			builder.Append(grapheme[run..i].ToUpperInvariant()).Append(expanded);
			run = i + 1;
		}
		return builder.Append(grapheme[run..].ToUpperInvariant()).ToString();
	}

	/// <summary>
	/// Every character whose JS `toUpperCase` is more than one character (generated from node's ICU; all are in the BMP),
	/// and dotless ı, which JS capitalises to I but .NET's invariant culture leaves alone.
	/// </summary>
	private static readonly Dictionary<char, string> FullUpper = new()
	{
		['ı'] = "I",
		['\u00DF'] = "\u0053\u0053", ['\u0149'] = "\u02BC\u004E", ['\u01F0'] = "\u004A\u030C", ['\u0390'] = "\u0399\u0308\u0301", ['\u03B0'] = "\u03A5\u0308\u0301", ['\u0587'] = "\u0535\u0552",
		['\u1E96'] = "\u0048\u0331", ['\u1E97'] = "\u0054\u0308", ['\u1E98'] = "\u0057\u030A", ['\u1E99'] = "\u0059\u030A", ['\u1E9A'] = "\u0041\u02BE", ['\u1F50'] = "\u03A5\u0313",
		['\u1F52'] = "\u03A5\u0313\u0300", ['\u1F54'] = "\u03A5\u0313\u0301", ['\u1F56'] = "\u03A5\u0313\u0342", ['\u1F80'] = "\u1F08\u0399", ['\u1F81'] = "\u1F09\u0399", ['\u1F82'] = "\u1F0A\u0399",
		['\u1F83'] = "\u1F0B\u0399", ['\u1F84'] = "\u1F0C\u0399", ['\u1F85'] = "\u1F0D\u0399", ['\u1F86'] = "\u1F0E\u0399", ['\u1F87'] = "\u1F0F\u0399", ['\u1F88'] = "\u1F08\u0399",
		['\u1F89'] = "\u1F09\u0399", ['\u1F8A'] = "\u1F0A\u0399", ['\u1F8B'] = "\u1F0B\u0399", ['\u1F8C'] = "\u1F0C\u0399", ['\u1F8D'] = "\u1F0D\u0399", ['\u1F8E'] = "\u1F0E\u0399",
		['\u1F8F'] = "\u1F0F\u0399", ['\u1F90'] = "\u1F28\u0399", ['\u1F91'] = "\u1F29\u0399", ['\u1F92'] = "\u1F2A\u0399", ['\u1F93'] = "\u1F2B\u0399", ['\u1F94'] = "\u1F2C\u0399",
		['\u1F95'] = "\u1F2D\u0399", ['\u1F96'] = "\u1F2E\u0399", ['\u1F97'] = "\u1F2F\u0399", ['\u1F98'] = "\u1F28\u0399", ['\u1F99'] = "\u1F29\u0399", ['\u1F9A'] = "\u1F2A\u0399",
		['\u1F9B'] = "\u1F2B\u0399", ['\u1F9C'] = "\u1F2C\u0399", ['\u1F9D'] = "\u1F2D\u0399", ['\u1F9E'] = "\u1F2E\u0399", ['\u1F9F'] = "\u1F2F\u0399", ['\u1FA0'] = "\u1F68\u0399",
		['\u1FA1'] = "\u1F69\u0399", ['\u1FA2'] = "\u1F6A\u0399", ['\u1FA3'] = "\u1F6B\u0399", ['\u1FA4'] = "\u1F6C\u0399", ['\u1FA5'] = "\u1F6D\u0399", ['\u1FA6'] = "\u1F6E\u0399",
		['\u1FA7'] = "\u1F6F\u0399", ['\u1FA8'] = "\u1F68\u0399", ['\u1FA9'] = "\u1F69\u0399", ['\u1FAA'] = "\u1F6A\u0399", ['\u1FAB'] = "\u1F6B\u0399", ['\u1FAC'] = "\u1F6C\u0399",
		['\u1FAD'] = "\u1F6D\u0399", ['\u1FAE'] = "\u1F6E\u0399", ['\u1FAF'] = "\u1F6F\u0399", ['\u1FB2'] = "\u1FBA\u0399", ['\u1FB3'] = "\u0391\u0399", ['\u1FB4'] = "\u0386\u0399",
		['\u1FB6'] = "\u0391\u0342", ['\u1FB7'] = "\u0391\u0342\u0399", ['\u1FBC'] = "\u0391\u0399", ['\u1FC2'] = "\u1FCA\u0399", ['\u1FC3'] = "\u0397\u0399", ['\u1FC4'] = "\u0389\u0399",
		['\u1FC6'] = "\u0397\u0342", ['\u1FC7'] = "\u0397\u0342\u0399", ['\u1FCC'] = "\u0397\u0399", ['\u1FD2'] = "\u0399\u0308\u0300", ['\u1FD3'] = "\u0399\u0308\u0301", ['\u1FD6'] = "\u0399\u0342",
		['\u1FD7'] = "\u0399\u0308\u0342", ['\u1FE2'] = "\u03A5\u0308\u0300", ['\u1FE3'] = "\u03A5\u0308\u0301", ['\u1FE4'] = "\u03A1\u0313", ['\u1FE6'] = "\u03A5\u0342", ['\u1FE7'] = "\u03A5\u0308\u0342",
		['\u1FF2'] = "\u1FFA\u0399", ['\u1FF3'] = "\u03A9\u0399", ['\u1FF4'] = "\u038F\u0399", ['\u1FF6'] = "\u03A9\u0342", ['\u1FF7'] = "\u03A9\u0342\u0399", ['\u1FFC'] = "\u03A9\u0399",
		['\uFB00'] = "\u0046\u0046", ['\uFB01'] = "\u0046\u0049", ['\uFB02'] = "\u0046\u004C", ['\uFB03'] = "\u0046\u0046\u0049", ['\uFB04'] = "\u0046\u0046\u004C", ['\uFB05'] = "\u0053\u0054",
		['\uFB06'] = "\u0053\u0054", ['\uFB13'] = "\u0544\u0546", ['\uFB14'] = "\u0544\u0535", ['\uFB15'] = "\u0544\u053B", ['\uFB16'] = "\u054E\u0546", ['\uFB17'] = "\u0544\u053D",
	};

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
