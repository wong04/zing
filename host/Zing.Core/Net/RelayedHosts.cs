using System.Net;
using System.Net.Sockets;

namespace Zing.Core.Net;

/// <summary>
/// What a relayed page may never reach. The relay is for getting at blocked public sites; a page it shows, or a site that
/// page redirects to, must not be a way into the user's own network (a router, a printer, an intranet server, this PC).
/// </summary>
public static class RelayedHosts
{
	private static readonly string[] OwnNetworkSuffixes = [".internal", ".lan"];

	/// <summary>Judged from the text alone: Open a Link's rule plus carrier-grade NAT, <c>.internal</c> and <c>.lan</c>.</summary>
	public static bool IsOwnNetworkHost(string host)
	{
		var name = host.ToLowerInvariant().TrimEnd('.');
		if (RelayRules.IsPrivateHostName(name)) return true;
		if (OwnNetworkSuffixes.Any(s => name.EndsWith(s, StringComparison.Ordinal))) return true;
		return IPAddress.TryParse(name.Trim('[', ']'), out var literal) && !IsPublicAddress(literal);
	}

	/// <summary>What a name resolved to: loopback, private, link-local, unspecified and carrier-grade NAT (100.64.0.0/10) are not public.</summary>
	public static bool IsPublicAddress(IPAddress address)
	{
		if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
		if (RelayRules.IsPrivateAddress(address)) return false;
		if (address.AddressFamily != AddressFamily.InterNetwork) return true;
		var b = address.GetAddressBytes();
		return !(b[0] == 100 && (b[1] & 0xC0) == 0x40);
	}
}

/// <summary>A fetch for a relayed page was aimed at the user's own network.</summary>
public sealed class OwnNetworkError() : RelayError("That address is on your own network, which a relayed page can't reach.");

/// <summary>
/// Where a transport may connect. <see cref="PublicOnly"/> is for fetching on a relayed page's behalf; Open a Link's own first
/// attempt, which the user typed, uses <see cref="Unrestricted"/>.
/// </summary>
public sealed class ConnectPolicy(Func<string, bool> allowsHost, Func<IPAddress, bool> allowsAddress)
{
	public static ConnectPolicy Unrestricted { get; } = new(_ => true, _ => true) { Checks = false };

	public static ConnectPolicy PublicOnly { get; } = new(host => !RelayedHosts.IsOwnNetworkHost(host), RelayedHosts.IsPublicAddress);

	private bool Checks { get; init; } = true;

	public void CheckHost(string host)
	{
		if (!allowsHost(host)) throw new OwnNetworkError();
	}

	public void CheckAddress(IPAddress address)
	{
		if (!allowsAddress(address)) throw new OwnNetworkError();
	}

	/// <summary>
	/// Resolves and checks every address, then connects: so a public name that resolves to a private address fails here, whatever the
	/// text said. Null for an unrestricted policy, which leaves the handler's own connecting alone.
	/// </summary>
	public Func<System.Net.Http.SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>>? ConnectCallback => Checks ? ConnectAsync : null;

	private async ValueTask<Stream> ConnectAsync(System.Net.Http.SocketsHttpConnectionContext context, CancellationToken ct)
	{
		var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
		foreach (var address in addresses) CheckAddress(address);
		var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
		try
		{
			await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, ct);
			return new NetworkStream(socket, ownsSocket: true);
		}
		catch
		{
			socket.Dispose();
			throw;
		}
	}
}
