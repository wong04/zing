using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace Zing.Core.Net;

/// <summary>The pure parts of the engine, twins of src/shared/unblock/relayEngine.ts held to it by relayEngine.vectors.json.</summary>
public static class RelayRules
{
	private static readonly char[] PastedWrappers = ['<', '>', '"', '\''];

	/// <summary>Accepts the sloppy things people paste: bare domains, stray whitespace, links copied inside angle brackets.</summary>
	public static Uri Normalize(string input)
	{
		var text = input.Trim().Trim(PastedWrappers);
		if (text == "") throw new BadURLError();
		if (!text.Contains("://")) text = "https://" + text;
		if (!Uri.TryCreate(text, UriKind.Absolute, out var url)) throw new BadURLError();
		if (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps) throw new BadURLError();
		if (url.Host == "") throw new BadURLError();
		if (url.UserInfo != "") throw new CredentialsInURLError();
		return url;
	}

	/// <summary>
	/// Whether the address names something on the user's own network, judged from the text alone (no lookup): a loopback, private or
	/// link-local address, localhost, a .local name, or a single-label name. Such an address must not be sent to a third party.
	/// </summary>
	public static bool IsPrivateHost(Uri url) => IsPrivateHostName(url.Host);

	/// <summary>The same for a host name as <see cref="Uri.Host"/> gives it (an IPv6 address in brackets). Trailing dots are ignored.</summary>
	public static bool IsPrivateHostName(string name)
	{
		var host = name.ToLowerInvariant().TrimEnd('.');
		if (IPAddress.TryParse(host.Trim('[', ']'), out var address)) return IsPrivateAddress(address);
		return host == "localhost" || host.EndsWith(".localhost", StringComparison.Ordinal) || host.EndsWith(".local", StringComparison.Ordinal) || !host.Contains('.');
	}

	public static bool IsPrivateAddress(IPAddress address)
	{
		if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
		if (address.AddressFamily == AddressFamily.InterNetworkV6)
		{
			var bytes = address.GetAddressBytes();
			// The deprecated IPv4-compatible form (::a.b.c.d) carries an IPv4 address in its last 32 bits.
			if (bytes[..12].All(b => b == 0) && !address.Equals(IPAddress.IPv6Any) && !address.Equals(IPAddress.IPv6Loopback)) return IsPrivateAddress(new IPAddress(bytes[12..]));
			return address.Equals(IPAddress.IPv6Loopback) || address.Equals(IPAddress.IPv6Any) || (bytes[0] & 0xFE) == 0xFC || (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80);
		}
		var b = address.GetAddressBytes();
		return b[0] is 0 or 10 or 127 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
	}

	/// <summary>Anything outside 200 to 399 is a failed fetch; redirects were already followed.</summary>
	public static void ValidateStatus(int status)
	{
		if (status < 200 || status >= 400) throw new HTTPStatusError(status);
	}

	/// <summary>
	/// The snapshot named by an archive.org/wayback/available answer. The API hands back http:// links that redirect, so only the
	/// snapshot's own scheme is upgraded; the archived page's address inside the path stays as it was.
	/// </summary>
	public static Uri WaybackSnapshot(JsonElement answer)
	{
		if (answer.ValueKind != JsonValueKind.Object
			|| !answer.TryGetProperty("archived_snapshots", out var snapshots) || snapshots.ValueKind != JsonValueKind.Object
			|| !snapshots.TryGetProperty("closest", out var closest) || closest.ValueKind != JsonValueKind.Object
			|| !closest.TryGetProperty("available", out var available) || available.ValueKind != JsonValueKind.True
			|| !closest.TryGetProperty("url", out var url) || url.ValueKind != JsonValueKind.String)
			throw new EmptyBodyError();
		var text = url.GetString()!;
		if (text.StartsWith("http://", StringComparison.Ordinal)) text = "https://" + text["http://".Length..];
		if (!Uri.TryCreate(text, UriKind.Absolute, out var snapshot)) throw new EmptyBodyError();
		return snapshot;
	}
}
