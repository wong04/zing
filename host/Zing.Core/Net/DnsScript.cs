using System.Net.Sockets;
using System.Text;

namespace Zing.Core.Net;

/// <summary>
/// The PowerShell that turns on encrypted DNS for this PC's adapters, and the one that puts the previous settings back. The person
/// reads the exact text before it runs as administrator. Adapter names, addresses and templates come from the system or from a file
/// on disk, so a value reaches the text only checked (<see cref="DnsAddresses"/>) or quoted as a single-quoted string.
/// </summary>
public static class DnsScript
{
	/// <summary>The script's exit code when this Windows lacks the encrypted-DNS commands; it had changed nothing.</summary>
	public const int NoEncryptedDnsExit = 2;

	/// <summary>Any other failure inside the script.</summary>
	public const int FailedExit = 3;

	// A command line holds about 32,000 characters and the encoded script is a little under three times the script.
	private const int MaxScriptLength = 10000;

	/// <summary>
	/// Reads the connected physical adapters (not VPN or virtual ones, whose DNS belongs to what made them), their IPv4 and IPv6 servers
	/// as set by hand, and the encrypted-DNS table. Changes nothing, and needs no administrator rights.
	/// </summary>
	public const string List = """
		$ErrorActionPreference = 'Stop'
		$v4Key = 'HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces'
		$v6Key = 'HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters\Interfaces'
		$supported = [bool](Get-Command Add-DnsClientDohServerAddress -ErrorAction SilentlyContinue)
		$adapters = @(foreach ($adapter in Get-NetAdapter -Physical | Where-Object { $_.Status -eq 'Up' }) {
			[pscustomobject]@{
				name = $adapter.Name
				v4 = (Get-ItemProperty -LiteralPath "$v4Key\$($adapter.InterfaceGuid)" -Name NameServer -ErrorAction SilentlyContinue).NameServer
				v6enabled = [bool](Get-NetAdapterBinding -Name ([WildcardPattern]::Escape($adapter.Name)) -ComponentID ms_tcpip6).Enabled
				v6 = (Get-ItemProperty -LiteralPath "$v6Key\$($adapter.InterfaceGuid)" -Name NameServer -ErrorAction SilentlyContinue).NameServer
			}
		})
		$doh = @()
		if (Get-Command Get-DnsClientDohServerAddress -ErrorAction SilentlyContinue) {
			$doh = @(Get-DnsClientDohServerAddress | ForEach-Object {
				[pscustomobject]@{ address = $_.ServerAddress; template = $_.DohTemplate; allowFallbackToUdp = [bool]$_.AllowFallbackToUdp; autoUpgrade = [bool]$_.AutoUpgrade }
			})
		}
		ConvertTo-Json -Compress -Depth 4 -InputObject @{ adapters = $adapters; doh = $doh; dohSupported = $supported }
		""";

	/// <summary>
	/// Registers the provider's servers for encrypted DNS and points every adapter at them. Registering matters: Windows ships these
	/// servers in its table with AutoUpgrade off, and a server is only asked over HTTPS when the table says to upgrade it.
	/// </summary>
	public static string Apply(string provider, IReadOnlyList<DnsAdapter> adapters)
	{
		var chosen = DnsProviders.Get(provider);
		if (adapters.Count == 0) throw new NoDnsAdaptersError();
		var template = Quote(DnsAddresses.Template(chosen.Template));
		var script = new Script();
		script.Line("$ErrorActionPreference = 'Stop'");
		script.Line("try {");
		script.Line($"if (-not (Get-Command Add-DnsClientDohServerAddress -ErrorAction SilentlyContinue)) {{ exit {NoEncryptedDnsExit} }}", 1);
		script.Line($"foreach ($server in {QuotedList(chosen.Servers)}) {{", 1);
		script.Line("if (Get-DnsClientDohServerAddress -ServerAddress $server -ErrorAction SilentlyContinue) {", 2);
		script.Line($"Set-DnsClientDohServerAddress -ServerAddress $server -DohTemplate {template} -AllowFallbackToUdp $false -AutoUpgrade $true", 3);
		script.Line("} else {", 2);
		script.Line($"Add-DnsClientDohServerAddress -ServerAddress $server -DohTemplate {template} -AllowFallbackToUdp $false -AutoUpgrade $true", 3);
		script.Line("}", 2);
		script.Line("}", 1);
		foreach (var adapter in adapters)
		{
			// IPv6 servers only where the adapter has IPv6; a family it lacks would fail the whole call.
			var servers = adapter.V6 is null ? chosen.V4 : chosen.V4.Concat(chosen.V6).ToList();
			FindAdapter(script, adapter.Name, 1);
			script.Line("if (-not $adapter) { throw 'An adapter that was shown is gone' }", 1);
			script.Line($"Set-DnsClientServerAddress -InterfaceIndex $adapter.ifIndex -ServerAddresses {QuotedList(servers)}", 1);
		}
		script.Line("Clear-DnsClientCache", 1);
		script.Line($"}} catch {{ exit {FailedExit} }}");
		script.Line("exit 0");
		return script.ToString();
	}

	/// <summary>
	/// Puts the adapters back as they were saved, hand-set servers as they were and the rest automatic, then the encrypted-DNS table as
	/// it was for the provider's servers. An adapter that is no longer there has nothing to restore and is skipped.
	/// </summary>
	public static string Undo(IReadOnlyList<DnsAdapter> before, IReadOnlyList<DohEntry> dohBefore, string provider)
	{
		var plan = PlanUndo(before, dohBefore, provider);
		var script = new Script();
		script.Line("$ErrorActionPreference = 'Stop'");
		script.Line("try {");
		foreach (var (name, servers) in plan.Adapters)
		{
			FindAdapter(script, name, 1);
			script.Line("if ($adapter) {", 1);
			// Reset first: it clears both families, and a set below puts back only what was set by hand.
			script.Line("Set-DnsClientServerAddress -InterfaceIndex $adapter.ifIndex -ResetServerAddresses", 2);
			if (servers.Count > 0) script.Line($"Set-DnsClientServerAddress -InterfaceIndex $adapter.ifIndex -ServerAddresses {QuotedList(servers)}", 2);
			script.Line("}", 1);
		}
		foreach (var entry in plan.Restored)
			script.Line($"Set-DnsClientDohServerAddress -ServerAddress {Quote(entry.Address)} -DohTemplate {Quote(entry.Template)} -AllowFallbackToUdp {Bool(entry.AllowFallbackToUdp)} -AutoUpgrade {Bool(entry.AutoUpgrade)}", 1);
		foreach (var server in plan.Removed)
			script.Line($"if (Get-DnsClientDohServerAddress -ServerAddress {Quote(server)} -ErrorAction SilentlyContinue) {{ Remove-DnsClientDohServerAddress -ServerAddress {Quote(server)} }}", 1);
		script.Line("Clear-DnsClientCache", 1);
		script.Line($"}} catch {{ exit {FailedExit} }}");
		script.Line("exit 0");
		return script.ToString();
	}

	/// <summary>
	/// What <see cref="Undo"/> will set, one readable line each, from the same checked data the script is built from. The saved file can
	/// be edited by anything running as the person, so this is what to read before pressing Undo.
	/// </summary>
	public static IReadOnlyList<string> UndoSummary(IReadOnlyList<DnsAdapter> before, IReadOnlyList<DohEntry> dohBefore, string provider)
	{
		var plan = PlanUndo(before, dohBefore, provider);
		return
		[
			.. plan.Adapters.Select(a => $"{DnsDisplay.Visible(a.Name)}: {(a.Servers.Count == 0 ? "automatic (DHCP)" : string.Join(", ", a.Servers))}"),
			.. plan.Restored.Select(e => $"{e.Address}: restored to {DnsDisplay.Visible(e.Template)} (UDP fallback {(e.AllowFallbackToUdp ? "allowed" : "not allowed")}, auto-upgrade {(e.AutoUpgrade ? "on" : "off")})"),
			.. plan.Removed.Select(server => $"{server}: removed from Windows' encrypted DNS list"),
		];
	}

	private sealed record UndoPlanned(List<(string Name, List<string> Servers)> Adapters, List<DohEntry> Restored, List<string> Removed);

	private static UndoPlanned PlanUndo(IReadOnlyList<DnsAdapter> before, IReadOnlyList<DohEntry> dohBefore, string provider)
	{
		var chosen = DnsProviders.Get(provider);
		var restored = dohBefore.Select(entry => entry with { Address = DnsAddresses.Normalize(entry.Address), Template = DnsAddresses.Template(entry.Template) }).ToList();
		var owned = chosen.Servers.ToHashSet();
		if (restored.FirstOrDefault(entry => !owned.Contains(entry.Address)) is { } stray) throw new InvalidDnsAddressError(stray.Address);
		var adapters = before.Select(adapter => (adapter.Name, Servers: adapter.V4.Select(a => DnsAddresses.Normalize(a, AddressFamily.InterNetwork)).Concat((adapter.V6 ?? []).Select(a => DnsAddresses.Normalize(a, AddressFamily.InterNetworkV6))).ToList())).ToList();
		var kept = restored.Select(entry => entry.Address).ToHashSet();
		return new UndoPlanned(adapters, restored, [.. chosen.Servers.Where(s => !kept.Contains(s))]);
	}

	/// <summary>
	/// Finds an adapter by its exact name. -Name and -InterfaceAlias treat * ? and [ ] as wildcards (Get-NetAdapter -Name 'Wi-F*' finds
	/// Wi-Fi), so a name could reach another adapter; -ceq compares the whole name as it is.
	/// </summary>
	private static void FindAdapter(Script script, string name, int depth) =>
		script.Line($"$adapter = Get-NetAdapter | Where-Object {{ $_.Name -ceq {Quote(name)} }}", depth);

	/// <summary>The argument for <c>powershell -EncodedCommand</c>: the script itself, so no file stands between what was read and what runs as administrator.</summary>
	public static string EncodedCommand(string script)
	{
		if (script.Length > MaxScriptLength) throw new DnsScriptTooLongError(script.Length);
		return Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
	}

	/// <summary>
	/// A PowerShell single-quoted string. Inside one nothing expands, and the only way out is a quote, so quotes are doubled. PowerShell
	/// also counts the curly quotes (U+2018 to U+201B) as quotes, so those are doubled too.
	/// </summary>
	public static string Quote(string value)
	{
		var text = new StringBuilder("'");
		foreach (var c in value)
		{
			text.Append(c);
			if (c is '\'' or '‘' or '’' or '‚' or '‛') text.Append(c);
		}
		return text.Append('\'').ToString();
	}

	private static string QuotedList(IEnumerable<string> addresses) => string.Join(", ", addresses.Select(a => Quote(a)));

	private static string Bool(bool value) => value ? "$true" : "$false";

	private sealed class Script
	{
		private readonly StringBuilder _text = new();

		public void Line(string line, int depth = 0) => _text.Append('\t', depth).Append(line).Append('\n');

		public override string ToString() => _text.ToString();
	}
}
