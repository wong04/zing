using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zing.Core.Net;

/// <summary>What kind of interference is happening, which determines what can defeat it. Serialised as the TS BlockKind strings.</summary>
[JsonConverter(typeof(BlockKindConverter))]
public enum BlockKind
{
	NotBlocked,
	DnsTampering,
	IpBlocked,
	TlsInterception,
	SiteDown,
	Inconclusive,
}

public sealed class BlockKindConverter() : JsonStringEnumConverter<BlockKind>(JsonNamingPolicy.CamelCase);

/// <summary>The shape of the TS DiagnosticReport (src/shared/unblock/blockDiagnostics.ts).</summary>
public sealed record DiagnosticReport(
	string Host,
	BlockKind Kind,
	IReadOnlyList<string> SystemDNS,
	IReadOnlyList<string> PublicDNS,
	bool TcpReachable,
	bool TlsSucceeded,
	IReadOnlyList<string> Steps);

/// <param name="SystemTlsOk">The address your resolver gave proved it owns the hostname via a valid certificate. This dominates everything else.</param>
/// <param name="Sinkholed">Your resolver returned a null or private address.</param>
/// <param name="SystemAnswerEmpty">Your resolver returned nothing at all.</param>
/// <param name="PublicTcpOk">The DoH-resolved address accepts a connection.</param>
/// <param name="PublicTlsOk">The DoH-resolved address completes a TLS handshake with a valid certificate.</param>
public sealed record BlockObservations(bool SystemTlsOk, bool Sinkholed, bool SystemAnswerEmpty, bool PublicTcpOk, bool PublicTlsOk);

/// <summary>The pure parts of the diagnosis, twins of src/shared/unblock/blockDiagnostics.ts held to it by shared vectors.</summary>
public static class BlockDiagnostics
{
	public static BlockKind Classify(BlockObservations observed)
	{
		// Proof of identity beats every other signal: an address that presents a valid certificate for the host is not being interfered with.
		if (observed.SystemTlsOk) return BlockKind.NotBlocked;

		// A completed handshake is itself proof the address is reachable.
		var reachable = observed.PublicTcpOk || observed.PublicTlsOk;

		if (observed.Sinkholed || observed.SystemAnswerEmpty) return reachable ? BlockKind.DnsTampering : BlockKind.IpBlocked;

		// Your answer can't prove itself but a public one works end to end, so the lie is in the resolver.
		if (observed.PublicTlsOk) return BlockKind.DnsTampering;

		// Reachable but the handshake dies: something is reading the server name and cutting the connection.
		if (reachable) return BlockKind.TlsInterception;

		return BlockKind.IpBlocked;
	}

	/// <summary>Addresses a filter hands out when it wants a name to go nowhere: unspecified, loopback, private and link-local.</summary>
	public static bool IsSinkhole(string ip)
	{
		// TryParse also takes shorthand like "10" (0.0.0.10), which no resolver answers with.
		if (!IPAddress.TryParse(ip, out var address) || (!ip.Contains(':') && ip.Count(c => c == '.') != 3)) return false;
		// Spelled as the TS twin does: A-record answers are IPv4, so longer spellings of ::1 are not worth parsing.
		if (address.AddressFamily == AddressFamily.InterNetworkV6) return ip is "::" or "::1";
		var bytes = address.GetAddressBytes();
		return bytes[0] == 0
			|| bytes[0] == 10
			|| bytes[0] == 127
			|| (bytes[0] == 169 && bytes[1] == 254)
			|| (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
			|| (bytes[0] == 192 && bytes[1] == 168);
	}

	/// <summary>Differing answers prove nothing (anycast CDNs answer per resolver), so this only ever becomes a note in the report.</summary>
	public static bool AnswersDiffer(IReadOnlyList<string> systemDns, IReadOnlyList<string> publicDns)
	{
		if (systemDns.Count == 0) return false;
		var publicSet = publicDns.ToHashSet();
		return systemDns.All(address => !publicSet.Contains(address));
	}

	/// <summary>The address worth asking for a certificate: the first one your network gave that isn't a sinkhole.</summary>
	public static string? SystemProbeTarget(IReadOnlyList<string> systemDns) => systemDns.FirstOrDefault(address => !IsSinkhole(address));
}
