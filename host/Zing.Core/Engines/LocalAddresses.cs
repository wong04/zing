using System.Net;
using System.Net.Sockets;

namespace Zing.Core.Engines;

/// <summary>
/// Whether an address names this PC or the local network. The rules twin src/shared/convert/ytdlpArgs.ts, and both are
/// held to src/shared/convert/mediaUrl.vectors.json. A public name that resolves to a private address, and the redirects
/// an engine follows, can't be seen from the text and are not covered.
/// </summary>
internal static class LocalAddresses
{
	private static readonly string[] LocalSuffixes = [".localhost", ".local", ".lan", ".internal", ".intranet", ".localdomain", ".home.arpa"];

	public static bool IsLocal(Uri uri)
	{
		if (uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6) return IPAddress.TryParse(uri.DnsSafeHost, out var ip) ? IsLocal(ip) : true;
		var name = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
		// A name with no dot is an intranet machine.
		return !name.Contains('.') || name == "home.arpa" || LocalSuffixes.Any(name.EndsWith);
	}

	public static bool IsLocal(IPAddress ip)
	{
		if (ip.AddressFamily == AddressFamily.InterNetwork) return IsLocalV4(ip.GetAddressBytes());
		var b = ip.GetAddressBytes();
		var first = (b[0] << 8) | b[1];
		var highZero = b.Take(10).All(x => x == 0);
		// IPv4-compatible (::a.b.c.d, which includes :: and ::1) and IPv4-mapped (::ffff:a.b.c.d) carry the address in the last 4 bytes.
		if (b.Take(12).All(x => x == 0) || (highZero && b[10] == 0xff && b[11] == 0xff)) return IsLocalV4(b[12..]);
		// NAT64 (64:ff9b::/96).
		if (first == 0x64 && b[2] == 0xff && b[3] == 0x9b && b[4..12].All(x => x == 0)) return IsLocalV4(b[12..]);
		// 6to4 (2002::/16) carries one in the next 4 bytes.
		if (first == 0x2002) return IsLocalV4(b[2..6]);
		return (first & 0xfe00) == 0xfc00 || (first & 0xffc0) == 0xfe80 || (first & 0xffc0) == 0xfec0 || (first & 0xff00) == 0xff00;
	}

	private static bool IsLocalV4(byte[] b) => b[0] switch
	{
		0 or 10 or 127 or >= 224 => true,
		100 => b[1] is >= 64 and <= 127,
		169 => b[1] == 254,
		172 => b[1] is >= 16 and <= 31,
		192 => b[1] == 168 || (b[1] == 0 && b[2] == 0),
		198 => b[1] is 18 or 19,
		_ => false,
	};
}
