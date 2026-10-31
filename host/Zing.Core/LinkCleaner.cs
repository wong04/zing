using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Zing.Core;

public sealed record CleanedLink(string Url, IReadOnlyList<string> Removed);

public sealed class InvalidLinkException(string message) : Exception(message);

/// <summary>Port of src/shared/browser/linkCleaner.ts.</summary>
public static class LinkCleaner
{
	public static readonly IReadOnlySet<string> TrackingParameters = new HashSet<string>(
		[
			"utm_source", "utm_medium", "utm_campaign", "utm_term", "utm_content",
			"utm_id", "utm_name", "utm_cid", "utm_reader", "utm_social",
			"fbclid", "gclid", "dclid", "gbraid", "wbraid", "msclkid", "twclid",
			"igshid", "igsh", "mc_cid", "mc_eid", "yclid", "_openstat",
			"ref", "ref_src", "ref_url", "referrer", "source",
			"si", "spm", "scm", "vero_id", "vero_conv", "wickedid",
			"oly_anon_id", "oly_enc_id", "__s", "trk", "trkCampaign",
			"ICID", "cmpid", "campaign_id", "ad_id", "adset_id",
		],
		StringComparer.Ordinal);

	private static readonly UTF8Encoding StrictUtf8 = new(false, true);

	private static readonly JsonSerializerOptions QuoteOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

	private static readonly char[] WrappingPunctuation = ['<', '>', '"', '\''];

	public static bool IsTrackingParameter(string name) =>
		TrackingParameters.Contains(name) || name.StartsWith("utm_", StringComparison.Ordinal);

	// A malformed escape can't spell a tracker's name, so it is compared as written (as the TS does).
	private static string ParameterName(string item)
	{
		var equals = item.IndexOf('=', StringComparison.Ordinal);
		var raw = equals < 0 ? item : item[..equals];
		return TryDecodeUriComponent(raw, out var decoded) ? decoded : raw;
	}

	/// <summary>JS `decodeURIComponent`: false on a malformed escape or invalid UTF-8.</summary>
	private static bool TryDecodeUriComponent(string text, out string decoded)
	{
		decoded = text;
		var builder = new StringBuilder();
		var pending = new List<byte>();
		var i = 0;
		while (i < text.Length) {
			if (text[i] != '%') {
				if (!Flush(pending, builder)) return false;
				builder.Append(text[i++]);
				continue;
			}
			if (i + 3 > text.Length) return false;
			if (!byte.TryParse(text.AsSpan(i + 1, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value)) return false;
			pending.Add(value);
			i += 3;
		}
		if (!Flush(pending, builder)) return false;
		decoded = builder.ToString();
		return true;
	}

	private static bool Flush(List<byte> pending, StringBuilder builder)
	{
		if (pending.Count == 0) return true;
		try {
			builder.Append(StrictUtf8.GetString([.. pending]));
		} catch (DecoderFallbackException) {
			return false;
		}
		pending.Clear();
		return true;
	}

	/// <summary>Removes tracking parameters, leaving every other part of the address exactly as written.</summary>
	public static CleanedLink RemoveTrackingParameters(string url)
	{
		var hashAt = url.IndexOf('#', StringComparison.Ordinal);
		var fragment = hashAt < 0 ? "" : url[hashAt..];
		var beforeFragment = hashAt < 0 ? url : url[..hashAt];
		var queryAt = beforeFragment.IndexOf('?', StringComparison.Ordinal);
		if (queryAt < 0) return new CleanedLink(url, []);

		var removed = new List<string>();
		var kept = new List<string>();
		foreach (var item in beforeFragment[(queryAt + 1)..].Split('&')) {
			var name = ParameterName(item);
			if (IsTrackingParameter(name)) removed.Add(name);
			else kept.Add(item);
		}
		if (removed.Count == 0) return new CleanedLink(url, removed);
		var query = kept.Count == 0 ? "" : "?" + string.Join('&', kept);
		return new CleanedLink(beforeFragment[..queryAt] + query + fragment, removed);
	}

	/// <summary>Surrounding whitespace and the quotes/brackets a link is wrapped in come off, and https:// goes in front when there's no scheme. Throws when there's no address with a host.</summary>
	public static string NormalizeLink(string input)
	{
		var text = AddressInput.JsTrim(input).Trim(WrappingPunctuation);
		if (text.Length == 0) throw NotALink(input);
		var candidate = text.Contains("://", StringComparison.Ordinal) ? text : "https://" + text;
		if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) || uri.Host.Length == 0) throw NotALink(input);
		return candidate;
	}

	private static InvalidLinkException NotALink(string input) =>
		new($"not a link: {JsonSerializer.Serialize(input, QuoteOptions)}");

	public static CleanedLink CleanLink(string input) => RemoveTrackingParameters(NormalizeLink(input));

	/// <summary>Only http(s) addresses are touched; anything else is returned as it is.</summary>
	public static string StripTrackers(string url)
	{
		var colon = url.IndexOf(':', StringComparison.Ordinal);
		if (colon < 1 || !char.IsAsciiLetter(url[0])) return url;
		var scheme = url[..colon];
		if (!scheme.All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '.' or '-')) return url;
		if (!scheme.Equals("http", StringComparison.OrdinalIgnoreCase) && !scheme.Equals("https", StringComparison.OrdinalIgnoreCase)) return url;
		return RemoveTrackingParameters(url).Url;
	}
}
