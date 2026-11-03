namespace Zing.Core.Net;

/// <summary>
/// The ladder: compare the network's DNS with a public resolver's, then test TCP, then test TLS. Each layer that works narrows down
/// where the interference sits (port of src/platform/net/diagnose.ts).
/// </summary>
public sealed class Diagnose(IProbes probes, IDohClient doh)
{
	private abstract record PublicAnswer;
	private sealed record Addresses(IReadOnlyList<string> Found) : PublicAnswer;
	private sealed record NxDomain : PublicAnswer;
	private sealed record Failed(string Reason) : PublicAnswer;

	/// <param name="typed">A site name, or an address copied from the address bar.</param>
	/// <param name="provider">The DoH provider setting's value.</param>
	public async Task<DiagnosticReport> RunAsync(string typed, string provider, CancellationToken ct)
	{
		var host = HostOf(typed);
		var steps = new List<string>();
		var systemLookup = probes.ResolveSystemAsync(host, ct);
		var publicLookup = LookUpPublicAsync(host, provider, ct);
		await Task.WhenAll(systemLookup, publicLookup);
		var systemDns = systemLookup.Result;
		steps.Add(systemDns.Count == 0 ? "Network DNS: no answer (possible NXDOMAIN block)" : $"Network DNS: {string.Join(", ", systemDns)}");

		switch (publicLookup.Result)
		{
			case NxDomain:
				steps.Add("Public DoH: NXDOMAIN, the name genuinely doesn't exist");
				return new DiagnosticReport(host, BlockKind.SiteDown, systemDns, [], false, false, steps);
			case Failed failed:
				return await WithoutPublicDnsAsync(host, systemDns, failed.Reason, steps, ct);
			case Addresses { Found: var publicDns }:
				return await LadderAsync(host, systemDns, publicDns, steps, ct);
			default:
				throw new InvalidOperationException("Unhandled public DNS answer");
		}
	}

	private async Task<PublicAnswer> LookUpPublicAsync(string host, string provider, CancellationToken ct)
	{
		try
		{
			return new Addresses(await doh.ResolveAsync(host, provider, ct));
		}
		catch (NxDomainError)
		{
			return new NxDomain();
		}
		// Networks often block or rewrite encrypted DNS. That is a result to explain, not a failure of the diagnosis.
		catch (ResolverFailedError e)
		{
			return new Failed(e.Message);
		}
	}

	/// <summary>Without a public answer the only proof available is a valid certificate on the address the network gave.</summary>
	private async Task<DiagnosticReport> WithoutPublicDnsAsync(string host, IReadOnlyList<string> systemDns, string reason, List<string> steps, CancellationToken ct)
	{
		steps.Add($"Public DoH: couldn't be used. {reason} Some networks block or rewrite encrypted DNS, so this says nothing about the site itself.");
		var proven = await ProvesIdentityAsync(host, systemDns, steps, ct);
		if (!proven) return new DiagnosticReport(host, BlockKind.Inconclusive, systemDns, [], false, false, steps);
		var kind = BlockDiagnostics.Classify(new BlockObservations(true, false, false, true, true));
		return new DiagnosticReport(host, kind, systemDns, [], true, true, steps);
	}

	private async Task<DiagnosticReport> LadderAsync(string host, IReadOnlyList<string> systemDns, IReadOnlyList<string> publicDns, List<string> steps, CancellationToken ct)
	{
		if (publicDns.Count == 0)
		{
			steps.Add("Public DoH: the name resolves but has no address records");
			return new DiagnosticReport(host, BlockKind.Inconclusive, systemDns, publicDns, false, false, steps);
		}

		steps.Add($"Public DoH: {string.Join(", ", publicDns)}");
		var target = publicDns[0];

		// A resolver that hands back a private or null address is the signature of a filter sinkholing the name.
		var sinkholed = systemDns.Any(BlockDiagnostics.IsSinkhole);
		if (sinkholed) steps.Add("Network DNS returned a sinkhole address, which is a deliberate block");
		if (BlockDiagnostics.AnswersDiffer(systemDns, publicDns)) steps.Add("Answers differ, which is normal for large CDNs, so not conclusive by itself");

		var systemTlsOk = await ProvesIdentityAsync(host, systemDns, steps, ct);

		var tcpOk = await probes.TcpAsync(target, ct);
		steps.Add(tcpOk ? "TCP 443: reachable" : "TCP 443: blocked or dropped");

		var tlsOk = false;
		if (tcpOk)
		{
			tlsOk = (await probes.TlsAsync(target, host, ct)).CertificateValid;
			steps.Add(tlsOk ? "TLS handshake: succeeded" : "TLS handshake: rejected");
		}

		var kind = BlockDiagnostics.Classify(new BlockObservations(systemTlsOk, sinkholed, systemDns.Count == 0, tcpOk, tlsOk));
		return new DiagnosticReport(host, kind, systemDns, publicDns, tcpOk, tlsOk, steps);
	}

	/// <summary>Whether the address the network gave presents a valid certificate for the host; false when there was nothing to ask.</summary>
	private async Task<bool> ProvesIdentityAsync(string host, IReadOnlyList<string> systemDns, List<string> steps, CancellationToken ct)
	{
		if (BlockDiagnostics.SystemProbeTarget(systemDns) is not { } systemTarget) return false;
		var valid = (await probes.TlsAsync(systemTarget, host, ct)).CertificateValid;
		steps.Add(valid ? $"Your network's address serves a valid certificate for {host}" : $"Your network's address failed to prove it's {host}");
		return valid;
	}

	private static string HostOf(string typed)
	{
		var text = typed.Trim();
		if (text == "") throw new InvalidHostError(typed);
		var candidate = text.Contains("://") ? text : $"https://{text}";
		if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) || uri.HostNameType != UriHostNameType.Dns) throw new InvalidHostError(typed);
		return uri.IdnHost.ToLowerInvariant();
	}
}
