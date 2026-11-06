using System.Text.Json;
using Zing.Core.Net;

namespace Zing.Host;

/// <summary>Probes that find what an e2e test says they find (TestHooks.NetServices), so a test never touches a network.</summary>
public sealed class FixedProbes(IReadOnlyList<string> system, bool tcp, bool tlsValid) : IProbes
{
	public static FixedProbes FromJson(string json)
	{
		using var document = JsonDocument.Parse(json);
		var root = document.RootElement;
		return new FixedProbes([.. root.GetProperty("system").EnumerateArray().Select(a => a.GetString()!)], root.GetProperty("tcp").GetBoolean(), root.GetProperty("tlsValid").GetBoolean());
	}

	public Task<IReadOnlyList<string>> ResolveSystemAsync(string host, CancellationToken ct) => Task.FromResult(system);

	public Task<bool> TcpAsync(string ip, CancellationToken ct) => Task.FromResult(tcp);

	public Task<TlsProbeResult> TlsAsync(string ip, string host, CancellationToken ct) =>
		Task.FromResult(new TlsProbeResult(tlsValid, tlsValid, tlsValid ? null : "refused by the test"));
}
