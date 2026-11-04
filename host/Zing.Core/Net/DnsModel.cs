using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Zing.Core.Net;

/// <summary>
/// One connected network adapter and the DNS servers set on it. Everything here comes from the system, so none of it is trusted: it only
/// reaches a script through <see cref="DnsScript"/>.
/// </summary>
/// <param name="V4">The IPv4 servers set by hand; empty when they come from the network (DHCP).</param>
/// <param name="V6">The same for IPv6; null when the adapter has IPv6 switched off, which is different from empty (automatic).</param>
public sealed record DnsAdapter(string Name, IReadOnlyList<string> V4, IReadOnlyList<string>? V6);

/// <summary>A server Windows is told to ask over HTTPS (Get-DnsClientDohServerAddress).</summary>
public sealed record DohEntry(string Address, string Template, bool AllowFallbackToUdp, bool AutoUpgrade);

/// <summary>The adapters and the encrypted-DNS table as Windows has them.</summary>
/// <param name="Doh">Only the entries for the providers' servers: an odd entry for any other server is none of this page's business.</param>
/// <param name="DohSupported">This Windows has the encrypted-DNS commands (Windows 11); without them the script can only fail.</param>
public sealed record DnsSnapshot(IReadOnlyList<DnsAdapter> Adapters, IReadOnlyList<DohEntry> Doh, bool DohSupported = true)
{
	/// <summary>Reads what <see cref="DnsScript.List"/> prints. Anything else is an error: a guess about a machine's DNS could be written back by Undo.</summary>
	public static DnsSnapshot Parse(string json)
	{
		try
		{
			using var document = JsonDocument.Parse(json);
			var root = document.RootElement;
			return new DnsSnapshot(
				[.. root.GetProperty("adapters").EnumerateArray().Select(ReadAdapter)],
				[.. root.GetProperty("doh").EnumerateArray().Where(IsProvidersServer).Select(ReadEntry)],
				root.GetProperty("dohSupported").GetBoolean());
		}
		catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or InvalidDnsAddressError or InvalidDohTemplateError)
		{
			throw new DnsListingError(error.Message, error);
		}
	}

	private static DnsAdapter ReadAdapter(JsonElement item)
	{
		var v6Enabled = item.GetProperty("v6enabled").GetBoolean();
		return new DnsAdapter(
			item.GetProperty("name").GetString() ?? throw new JsonException("an adapter has no name"),
			Servers(item.GetProperty("v4"), AddressFamily.InterNetwork),
			v6Enabled ? Servers(item.GetProperty("v6"), AddressFamily.InterNetworkV6) : null);
	}

	/// <summary>The registry keeps a family's servers as one string separated by commas or spaces; none set is null or empty.</summary>
	private static List<string> Servers(JsonElement value, AddressFamily family) =>
		value.ValueKind == JsonValueKind.Null
			? []
			: [.. (value.GetString() ?? "").Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(a => DnsAddresses.Normalize(a, family))];

	private static bool IsProvidersServer(JsonElement item) =>
		item.TryGetProperty("address", out var address) && address.ValueKind == JsonValueKind.String && DnsProviders.IsKnownServer(address.GetString()!);

	private static DohEntry ReadEntry(JsonElement item)
	{
		var address = DnsAddresses.Normalize(item.GetProperty("address").GetString() ?? "");
		return new DohEntry(address, DnsAddresses.Template(item.GetProperty("template").GetString() ?? ""), item.GetProperty("allowFallbackToUdp").GetBoolean(), item.GetProperty("autoUpgrade").GetBoolean());
	}
}

/// <summary>The only way a value from the system becomes part of a script: checked as an address or a template, or quoted.</summary>
public static partial class DnsAddresses
{
	[GeneratedRegex("^[0-9A-Fa-f:.]+$")]
	private static partial Regex Characters();

	/// <summary>The address in the form the system prints it. Refuses anything that is not an IP address, whatever it contains.</summary>
	public static string Normalize(string value) => TryNormalize(value, out var normal) ? normal : throw new InvalidDnsAddressError(value);

	public static bool TryNormalize(string value, out string normal)
	{
		normal = "";
		if (!Characters().IsMatch(value) || !IPAddress.TryParse(value, out var address)) return false;
		// "1" parses as 0.0.0.1; a server is written out in full.
		if (address.AddressFamily == AddressFamily.InterNetwork && value.Count(c => c == '.') != 3) return false;
		normal = address.ToString();
		return true;
	}

	public static string Normalize(string value, AddressFamily family)
	{
		var normal = Normalize(value);
		if (IPAddress.Parse(normal).AddressFamily != family) throw new InvalidDnsAddressError(value);
		return normal;
	}

	/// <summary>A DoH template is an https address; its quoting is the script's job, but a value that is not one is refused rather than quoted.</summary>
	public static string Template(string value)
	{
		if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || value.Any(c => char.IsControl(c) || char.IsWhiteSpace(c))) throw new InvalidDohTemplateError(value);
		return value;
	}
}

/// <summary>Reads and changes the machine's DNS. The Windows host implements it; a test passes a fake.</summary>
public interface IDnsSystem
{
	/// <summary>The connected adapters and the encrypted-DNS table, read without changing anything or asking for anything.</summary>
	Task<DnsSnapshot> ReadAsync(CancellationToken ct);

	/// <summary>Runs a script as administrator. Windows shows its prompt; saying no is <see cref="ElevatedRun.Declined"/>, not an error.</summary>
	Task<ElevatedRun> RunElevatedAsync(string script, CancellationToken ct);
}

/// <summary>How an elevated run ended.</summary>
public abstract record ElevatedRun
{
	private ElevatedRun()
	{
	}

	/// <summary>The person said no to the admin prompt, so the script never ran.</summary>
	public static readonly ElevatedRun Declined = new DeclinedRun();

	public sealed record Exited(int Code) : ElevatedRun;

	private sealed record DeclinedRun : ElevatedRun;
}
