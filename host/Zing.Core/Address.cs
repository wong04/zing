using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace Zing.Core;

/// <summary>What was typed: a place to go (as typed, with a scheme added when it had none) or something to search for.</summary>
public abstract record AddressInputResult
{
	public sealed record Url(string Value) : AddressInputResult;

	public sealed record Search(string Text) : AddressInputResult;
}

/// <summary>Port of src/shared/browser/addressInput.ts.</summary>
public static class AddressInput
{
	/// <summary>JS `\s` and `String.prototype.trim` whitespace, which differs from char.IsWhiteSpace (U+0085, U+001C-1F, U+FEFF).</summary>
	public static bool IsJsWhitespace(char c) =>
		c is '\t' or '\n' or '\v' or '\f' or '\r' or ' ' or '\u00a0' or '\u1680' or '\u2028' or '\u2029' or '\u202f' or '\u205f' or '\u3000' or '\ufeff'
		|| c is >= '\u2000' and <= '\u200a';

	public static string JsTrim(string text)
	{
		var start = 0;
		var end = text.Length;
		while (start < end && IsJsWhitespace(text[start])) start++;
		while (end > start && IsJsWhitespace(text[end - 1])) end--;
		return text[start..end];
	}

	/// <summary>Approximates `URL.canParse` for the candidates classify builds.</summary>
	public static bool CanParse(string candidate) => Uri.TryCreate(candidate, UriKind.Absolute, out _);

	private static AddressInputResult UrlOrSearch(string candidate, string text) =>
		CanParse(candidate) ? new AddressInputResult.Url(candidate) : new AddressInputResult.Search(text);

	public static bool IsIPv4(string host)
	{
		var parts = host.Split('.');
		return parts.Length == 4 && parts.All(IsOctet);
	}

	// Mirrors /^[+-]?[0-9]+$/ with Number(part) in 0..255 and at most 3 characters.
	private static bool IsOctet(string part)
	{
		if (part.Length > 3) return false;
		var digits = part.Length > 0 && part[0] is '+' or '-' ? part[1..] : part;
		if (digits.Length == 0 || !digits.All(char.IsAsciiDigit)) return false;
		return part[0] != '-' && int.Parse(digits, CultureInfo.InvariantCulture) <= 255 || digits.All(d => d == '0');
	}

	/// <summary>"[::1]" or "[2001:db8::1]:8080": an IPv6 literal in brackets, then a port or nothing.</summary>
	private static bool IsBracketedIPv6WithPort(string host)
	{
		var close = host.IndexOf(']');
		if (close < 0) return false;
		var literal = host[1..close];
		if (!literal.Contains(':') || !IPAddress.TryParse(literal, out var address) || address.AddressFamily != AddressFamily.InterNetworkV6) return false;
		var rest = host[(close + 1)..];
		return rest.Length == 0 || rest[0] == ':' && AllRunes(rest[1..], IsNumber);
	}

	// Iterates code points: JS's /u regexes match astral letters and digits, UTF-16 units would not.
	private static bool AllRunes(string text, Func<Rune, bool> test) => text.Length > 0 && text.EnumerateRunes().All(test);

	private static bool IsNumber(Rune rune) =>
		Rune.GetUnicodeCategory(rune) is UnicodeCategory.DecimalDigitNumber or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber;

	private static bool IsHostLabel(string label) => AllRunes(label, r => Rune.IsLetter(r) || IsNumber(r) || r.Value == '-');

	/// <summary>Null when there is nothing but whitespace.</summary>
	public static AddressInputResult? Classify(string raw)
	{
		var text = JsTrim(raw);
		if (text.Length == 0) return null;
		var search = new AddressInputResult.Search(text);
		if (text.Any(IsJsWhitespace)) return search;

		if (text.Contains("://", StringComparison.Ordinal)) return UrlOrSearch(text, text);
		if (text.StartsWith("about:", StringComparison.OrdinalIgnoreCase) || text.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) return UrlOrSearch(text, text);

		var hostEnd = text.IndexOfAny(['/', '?', '#']);
		var host = (hostEnd < 0 ? text : text[..hostEnd]).ToLowerInvariant();
		if (host.StartsWith('[')) return IsBracketedIPv6WithPort(host) ? UrlOrSearch("http://" + text, text) : search;
		var hasPort = false;
		var colon = host.LastIndexOf(':');
		if (colon >= 0) {
			if (!AllRunes(host[(colon + 1)..], IsNumber)) return search;
			host = host[..colon];
			hasPort = true;
		}

		if (host == "localhost" || IsIPv4(host)) return UrlOrSearch("http://" + text, text);

		var labels = host.Split('.');
		if (labels.Length < 2 || !labels.All(IsHostLabel)) return search;
		if (!hasPort && !IsTopLevelDomain(labels[^1])) return search;
		return UrlOrSearch("https://" + text, text);
	}

	/// <summary>
	/// A top-level domain by the Public Suffix List, so a new one (remix.run, astro.build) opens without a list to keep, while
	/// node.js and file.txt are still searched. Also the names that only exist on a home or company network.
	/// </summary>
	public static bool IsTopLevelDomain(string tld) => PrivateNames.Contains(tld.ToLowerInvariant()) || PublicSuffix.IsTopLevelDomain(tld);

	private static readonly IReadOnlySet<string> PrivateNames = new HashSet<string>(["local", "lan", "home", "internal", "test", "localhost"], StringComparer.Ordinal);
}

/// <summary>What typing in the address bar opens (src/main/address.ts). Engine URLs come from searchEngines.ts via Defaults.</summary>
public sealed class Address(IReadOnlyDictionary<string, string> searchUrls)
{
	private const string UriComponentUnreserved = "-_.!~*'()";

	public static Address FromDefaults()
	{
		var engines = ZingJson.LoadDefaults("search-engines.json").AsObject();
		return new Address(engines.ToDictionary(e => e.Key, e => e.Value!.GetValue<string>(), StringComparer.Ordinal));
	}

	public sealed class UnknownSearchEngineException(string engine) : Exception($"Unknown search engine {engine}");

	public sealed class BlankAddressException() : ArgumentException("There is nothing to open: the address is blank");

	/// <summary>The URL to load: a place to go or a search on the chosen engine. Throws for blank input. The tab strips trackers (TabService.CleanLink).</summary>
	public string Resolve(string input, string searchEngine)
	{
		var result = AddressInput.Classify(input) ?? throw new BlankAddressException();
		if (result is AddressInputResult.Url url) return url.Value;
		if (!searchUrls.TryGetValue(searchEngine, out var search)) throw new UnknownSearchEngineException(searchEngine);
		return search + EncodeUriComponent(((AddressInputResult.Search)result).Text);
	}

	/// <summary>JS `encodeURIComponent`: unlike Uri.EscapeDataString it leaves !'()* alone. Throws on a lone surrogate, as JS does.</summary>
	public static string EncodeUriComponent(string text)
	{
		var bytes = new UTF8Encoding(false, true).GetBytes(text);
		var builder = new StringBuilder();
		foreach (var b in bytes) {
			if (b < 0x80 && (char.IsAsciiLetterOrDigit((char)b) || UriComponentUnreserved.Contains((char)b))) builder.Append((char)b);
			else builder.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
		}
		return builder.ToString();
	}
}
