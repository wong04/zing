using System.Text;
using System.Text.RegularExpressions;

namespace Zing.Core.Net;

/// <summary>
/// Twin of src/shared/unblock/relayedWebContent.ts, held to it by relayedWebContent.vectors.json: the https to
/// relay-scheme mapping and the rewriting that keeps a relayed page's absolute links inside the relay.
/// Because the document itself loads from zing-relay://, relative and protocol-relative references resolve back into
/// the scheme on their own; only absolute https links need patching.
/// </summary>
public static partial class RelayedContent
{
	public const string Scheme = "zing-relay";

	private const string Prefix = Scheme + ":";

	public static bool IsRelayUrl(string url) => url.StartsWith(Prefix + "//", StringComparison.OrdinalIgnoreCase);

	/// <summary>Only https maps onto the relay scheme, which decodes back to https.</summary>
	public static Uri? Encode(Uri web)
	{
		if (web.Scheme != Uri.UriSchemeHttps) return null;
		return Uri.TryCreate(Prefix + web.AbsoluteUri["https:".Length..], UriKind.Absolute, out var relay) ? relay : null;
	}

	public static Uri? Decode(string relay)
	{
		if (!IsRelayUrl(relay)) return null;
		return Uri.TryCreate("https:" + relay[Prefix.Length..], UriKind.Absolute, out var web) ? web : null;
	}

	/// <summary>The real address a relay URL stands for, and anything else as it is. What a saved session keeps.</summary>
	public static string ToWebAddress(string url) => Decode(url)?.AbsoluteUri ?? url;

	[GeneratedRegex("charset=([^;]*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex CharsetParameter();

	/// <summary>Null for non-text, which is treated as binary. Stops at ';' so "charset=utf-8; format=flowed" is utf-8.</summary>
	public static string? Charset(string contentType)
	{
		var raw = CharsetParameter().Match(contentType) is { Success: true } match ? match.Groups[1].Value.Trim(' ', '\t', '\r', '\n', '"', '\'', ';') : "";
		if (raw != "") return raw;
		return contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ? "utf-8" : null;
	}

	public static bool IsPatchable(string contentType)
	{
		var kind = contentType.ToLowerInvariant();
		return kind.Contains("html") || kind.Contains("css");
	}

	private static readonly Regex[] AbsoluteReferences =
	[
		new(@"((?:src|href|poster|data-src|srcset|action)\s*=\s*[""'])https://", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
		new(@"(url\(\s*[""']?)https://", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
		new(@"(,\s*)https://", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
	];

	/// <summary>A base tag would re-anchor every relative URL back at https and undo the arrangement.</summary>
	[GeneratedRegex("<base[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex BaseTag();

	public static string PatchText(string text)
	{
		var pointed = AbsoluteReferences.Aggregate(text, (current, pattern) => pattern.Replace(current, "${1}" + Scheme + "://"));
		return BaseTag().Replace(pointed, "");
	}

	[GeneratedRegex("^(utf-?16|utf-?32|ucs-?2|ucs-?4)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex NonAsciiCompatible();

	/// <summary>Rewrites an HTML or CSS body byte for byte (Latin-1), so no charset is decoded wrongly; UTF-16/32 bodies come back as they were.</summary>
	public static byte[] Patch(byte[] body, string contentType)
	{
		if (!IsPatchable(contentType)) return body;
		if (NonAsciiCompatible().IsMatch(Charset(contentType) ?? "")) return body;
		var latin1 = Encoding.Latin1;
		return latin1.GetBytes(PatchText(latin1.GetString(body)));
	}

	/// <summary>Plenty of script and no visible text: a client-rendered app that died on a browser API the relay can't provide.</summary>
	public static bool IsAppLikeFailure(int visibleTextLength, int scriptCount) => visibleTextLength < 200 && scriptCount > 20;
}
