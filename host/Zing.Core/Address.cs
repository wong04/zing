using System.Globalization;
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

	public static bool IsTopLevelDomain(string tld)
	{
		var t = tld.ToLowerInvariant();
		return CountryTlds.Contains(t) || GenericTlds.Contains(t);
	}

	private static string[] Words(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

	public static readonly IReadOnlySet<string> CountryTlds = new HashSet<string>(
		Words("""
		ac ad ae af ag ai al am ao aq ar as at au aw ax az ba bb bd be bf bg bh bi bj bm bn bo
		br bs bt bw by bz ca cc cd cf cg ch ci ck cl cm cn co cr cu cv cw cx cy cz de dj dk dm
		do dz ec ee eg er es et eu fi fj fk fm fo fr ga gd ge gf gg gh gi gl gm gn gp gq gr gs
		gt gu gw gy hk hm hn hr ht hu id ie il im in io iq ir is it je jm jo jp ke kg kh ki km
		kn kp kr kw ky kz la lb lc li lk lr ls lt lu lv ly ma mc md me mg mh mk ml mm mn mo mp
		mq mr ms mt mu mv mw mx my mz na nc ne nf ng ni nl no np nr nu nz om pa pe pf pg ph pk
		pl pm pn pr ps pt pw py qa re ro rs ru rw sa sb sc sd se sg sh si sk sl sm sn so sr ss
		st su sv sx sy sz tc td tf tg th tj tk tl tm tn to tr tt tv tw tz ua ug uk us uy uz va
		vc ve vg vi vn vu wf ws ye yt za zm zw
		"""),
		StringComparer.Ordinal);

	public static readonly IReadOnlySet<string> GenericTlds = new HashSet<string>(
		Words("""
		com net org edu gov mil int info biz name pro
		io ai app dev page cloud tech online site website
		store shop blog news live art design xyz top club
		today world space studio media network systems software
		digital agency email solutions services academy school
		college university museum travel health law finance bank
		money games game fun wiki social chat video music
		photo photos gallery film tv radio zone one link
		click help support guide tips how new plus inc
		ltd llc gmbh group global earth city land moe
		dog cat eco green lol wtf rocks ninja codes
		google apple microsoft amazon youtube gle goog mozilla
		local lan home internal test localhost onion arpa coop aero
		"""),
		StringComparer.Ordinal);
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

	/// <summary>The URL to load: a place to go (tracker-free) or a search on the chosen engine. Throws for blank input.</summary>
	public string Resolve(string input, string searchEngine)
	{
		var result = AddressInput.Classify(input) ?? throw new BlankAddressException();
		if (result is AddressInputResult.Url url) return LinkCleaner.StripTrackers(url.Value);
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
