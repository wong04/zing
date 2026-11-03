using System.Text.Json;
using Zing.Core.Net;

namespace Zing.Host;

/// <summary>
/// The DNS of an e2e run (TestHooks.DnsSystem), so no test reads a real adapter, shows a UAC prompt or changes the machine's DNS.
/// It starts with two adapters; a test changes them and what a run ends with through the "dns-stub" test action. Every script it
/// is asked to run is appended to <c>dns-ran.jsonl</c> in the run's scratch profile and then does nothing.
/// </summary>
public sealed class StubDnsSystem : IDnsSystem
{
	private const string StartingAdapters = """
		{"adapters":[{"name":"Ethernet","v4":"10.0.0.1","v6enabled":true,"v6":""},{"name":"Wi-Fi","v4":"","v6enabled":false,"v6":null}],
		 "doh":[{"address":"1.1.1.1","template":"https://cloudflare-dns.com/dns-query","allowFallbackToUdp":false,"autoUpgrade":false}]}
		""";

	private DnsSnapshot _snapshot = DnsSnapshot.Parse(StartingAdapters);
	private ElevatedRun _result = new ElevatedRun.Exited(0);

	public Task<DnsSnapshot> ReadAsync(CancellationToken ct) => Task.FromResult(_snapshot);

	public Task<ElevatedRun> RunElevatedAsync(string script, CancellationToken ct)
	{
		File.AppendAllText(Path.Combine(AppPaths.UserData, "dns-ran.jsonl"), JsonSerializer.Serialize(new { script }) + "\n");
		return Task.FromResult(_result);
	}

	/// <summary>{adapters?: the listing script's JSON as a string, run?: "ok" | "declined" | "fail" | "no-doh"}.</summary>
	public void Configure(JsonElement message)
	{
		if (message.TryGetProperty("adapters", out var adapters)) _snapshot = DnsSnapshot.Parse(adapters.GetString()!);
		if (!message.TryGetProperty("run", out var run)) return;
		_result = run.GetString() switch
		{
			"ok" => new ElevatedRun.Exited(0),
			"declined" => ElevatedRun.Declined,
			"fail" => new ElevatedRun.Exited(DnsScript.FailedExit),
			"no-doh" => new ElevatedRun.Exited(DnsScript.NoEncryptedDnsExit),
			var other => throw new InvalidOperationException($"Unknown dns-stub run {other}"),
		};
	}
}
