using System.Text.Json;
using Zing.Core.Net;

namespace Zing.Core.Tests.Net;

public sealed class DiagnoseTests
{
	private sealed class FakeProbes : IProbes
	{
		public IReadOnlyList<string> System { get; init; } = [];
		/// <summary>Addresses that accept a connection on 443.</summary>
		public IReadOnlyList<string> Reachable { get; init; } = [];
		/// <summary>Addresses that present a valid certificate for the host.</summary>
		public IReadOnlyList<string> Valid { get; init; } = [];
		public bool LookupTimesOut { get; init; }
		/// <summary>Connections never complete; only the token ends them.</summary>
		public bool Hangs { get; init; }

		public Task<IReadOnlyList<string>> ResolveSystemAsync(string host, CancellationToken ct) =>
			LookupTimesOut ? Task.FromException<IReadOnlyList<string>>(new SystemLookupTimedOutError(host)) : Task.FromResult(System);

		public async Task<bool> TcpAsync(string ip, CancellationToken ct)
		{
			if (Hangs) await Task.Delay(Timeout.Infinite, ct);
			return Reachable.Contains(ip);
		}

		public Task<TlsProbeResult> TlsAsync(string ip, string host, CancellationToken ct) =>
			Task.FromResult(new TlsProbeResult(Valid.Contains(ip), Valid.Contains(ip), Valid.Contains(ip) ? null : "refused"));
	}

	private sealed class FakeDoh(Func<string, IReadOnlyList<string>> answer) : IDohClient
	{
		public string? ProviderAsked { get; private set; }

		public Task<IReadOnlyList<string>> ResolveAsync(string host, string provider, CancellationToken ct)
		{
			ProviderAsked = provider;
			return Task.FromResult(answer(host));
		}
	}

	private sealed class FailingDoh(Exception error) : IDohClient
	{
		public Task<IReadOnlyList<string>> ResolveAsync(string host, string provider, CancellationToken ct) => Task.FromException<IReadOnlyList<string>>(error);
	}

	private static Task<DiagnosticReport> Run(FakeProbes probes, IDohClient doh, string host = "example.com") =>
		new Diagnose(probes, doh).RunAsync(host, "cloudflare", CancellationToken.None);

	private static FakeDoh Answers(params string[] addresses) => new(_ => addresses);

	[Fact]
	public async Task A_valid_certificate_on_the_address_your_network_gave_is_not_blocked()
	{
		var report = await Run(new FakeProbes { System = ["93.184.216.34"], Valid = ["93.184.216.34"], Reachable = ["93.184.216.34"] }, Answers("93.184.216.34"));
		Assert.Equal(BlockKind.NotBlocked, report.Kind);
		Assert.Equal(["93.184.216.34"], report.SystemDNS);
		Assert.Equal(["93.184.216.34"], report.PublicDNS);
		Assert.True(report.TcpReachable);
		Assert.True(report.TlsSucceeded);
		Assert.Contains("Network DNS: 93.184.216.34", report.Steps);
	}

	[Fact]
	public async Task A_sinkholed_name_that_resolves_publicly_is_DNS_tampering()
	{
		var report = await Run(new FakeProbes { System = ["10.0.0.1"], Reachable = ["1.2.3.4"], Valid = ["1.2.3.4"] }, Answers("1.2.3.4"));
		Assert.Equal(BlockKind.DnsTampering, report.Kind);
		Assert.Contains(report.Steps, s => s.Contains("sinkhole"));
	}

	[Fact]
	public async Task Nothing_reaching_the_address_is_an_IP_block()
	{
		var report = await Run(new FakeProbes { System = ["1.2.3.4"] }, Answers("1.2.3.4"));
		Assert.Equal(BlockKind.IpBlocked, report.Kind);
		Assert.False(report.TcpReachable);
		Assert.False(report.TlsSucceeded);
	}

	[Fact]
	public async Task A_connection_that_dies_in_the_handshake_is_TLS_interception()
	{
		var report = await Run(new FakeProbes { System = ["1.2.3.4"], Reachable = ["1.2.3.4"] }, Answers("1.2.3.4"));
		Assert.Equal(BlockKind.TlsInterception, report.Kind);
		Assert.True(report.TcpReachable);
		Assert.False(report.TlsSucceeded);
	}

	[Fact]
	public async Task NXDOMAIN_from_the_public_resolver_means_the_site_is_down()
	{
		var report = await Run(new FakeProbes(), new FailingDoh(new NxDomainError("gone.example")), "gone.example");
		Assert.Equal(BlockKind.SiteDown, report.Kind);
		Assert.Empty(report.PublicDNS);
	}

	[Fact]
	public async Task No_answer_from_your_network_is_noted_and_does_not_stop_the_ladder()
	{
		var report = await Run(new FakeProbes { Reachable = ["1.2.3.4"], Valid = ["1.2.3.4"] }, Answers("1.2.3.4"));
		Assert.Equal(BlockKind.DnsTampering, report.Kind);
		Assert.Contains(report.Steps, s => s.StartsWith("Network DNS: no answer"));
	}

	[Fact]
	public async Task Encrypted_DNS_that_cannot_be_used_is_explained_and_the_verdict_rests_on_what_could_be_checked()
	{
		var report = await Run(new FakeProbes { System = ["1.2.3.4"] }, new FailingDoh(new ResolverUnreachableError("Cloudflare", "connection reset")));
		Assert.Equal(BlockKind.Inconclusive, report.Kind);
		Assert.Contains(report.Steps, s => s.Contains("Public DoH: couldn't be used") && s.Contains("connection reset") && s.Contains("encrypted DNS"));
	}

	[Fact]
	public async Task A_valid_certificate_still_proves_the_site_when_encrypted_DNS_cannot_be_used()
	{
		var report = await Run(new FakeProbes { System = ["1.2.3.4"], Valid = ["1.2.3.4"] }, new FailingDoh(new ResolverStatusError(2)));
		Assert.Equal(BlockKind.NotBlocked, report.Kind);
		Assert.Contains(report.Steps, s => s.Contains("Public DoH: couldn't be used"));
	}

	[Fact]
	public async Task A_garbled_answer_from_the_resolver_is_a_finding_not_a_crash()
	{
		var report = await Run(new FakeProbes { System = ["1.2.3.4"] }, new FailingDoh(new MalformedDoHResponseError("not JSON")));
		Assert.Equal(BlockKind.Inconclusive, report.Kind);
	}

	[Fact]
	public async Task A_name_with_no_public_address_is_inconclusive()
	{
		var report = await Run(new FakeProbes { System = ["1.2.3.4"] }, Answers());
		Assert.Equal(BlockKind.Inconclusive, report.Kind);
	}

	[Fact]
	public async Task Asks_the_provider_it_was_given()
	{
		var doh = Answers("1.2.3.4");
		await new Diagnose(new FakeProbes(), doh).RunAsync("example.com", "quad9", CancellationToken.None);
		Assert.Equal("quad9", doh.ProviderAsked);
	}

	[Theory]
	[InlineData("Example.COM", "example.com")]
	[InlineData("  example.com  ", "example.com")]
	[InlineData("https://Example.com/a/b?c=1", "example.com")]
	[InlineData("example.com:8443", "example.com")]
	public async Task Takes_the_host_from_whatever_was_typed(string typed, string host)
	{
		var report = await Run(new FakeProbes(), Answers(), typed);
		Assert.Equal(host, report.Host);
	}

	[Theory]
	[InlineData("")]
	[InlineData("   ")]
	[InlineData("http://")]
	[InlineData("93.184.216.34")]
	[InlineData("a b.com")]
	public async Task Refuses_what_is_not_a_site_name(string typed) =>
		await Assert.ThrowsAsync<InvalidHostError>(() => Run(new FakeProbes(), Answers(), typed));

	[Fact]
	public async Task A_system_lookup_that_times_out_is_a_step_not_an_error()
	{
		var report = await Run(new FakeProbes { LookupTimesOut = true, Reachable = ["1.2.3.4"], Valid = ["1.2.3.4"] }, Answers("1.2.3.4"));
		Assert.Equal(BlockKind.DnsTampering, report.Kind);
		Assert.Contains(report.Steps, s => s.StartsWith("Network DNS: no answer in time"));
	}

	[Fact]
	public async Task The_whole_diagnosis_has_a_deadline()
	{
		var diagnose = new Diagnose(new FakeProbes { Hangs = true, System = ["1.2.3.4"] }, Answers("1.2.3.4"), TimeSpan.FromMilliseconds(200));
		await Assert.ThrowsAsync<DiagnosisTimedOutError>(() => diagnose.RunAsync("example.com", "cloudflare", CancellationToken.None));
	}

	[Fact]
	public async Task A_cancelled_diagnosis_is_cancelled_not_timed_out()
	{
		using var cancel = new CancellationTokenSource();
		await cancel.CancelAsync();
		var diagnose = new Diagnose(new FakeProbes { Hangs = true, System = ["1.2.3.4"] }, Answers("1.2.3.4"));
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => diagnose.RunAsync("example.com", "cloudflare", cancel.Token));
	}

	[Fact]
	public async Task The_report_serialises_in_the_shape_of_the_TS_DiagnosticReport()
	{
		var report = await Run(new FakeProbes { System = ["1.2.3.4"], Valid = ["1.2.3.4"] }, Answers("1.2.3.4"));
		var json = JsonDocument.Parse(JsonSerializer.Serialize(report, ZingJson.Options)).RootElement;
		Assert.Equal(["host", "kind", "publicDNS", "steps", "systemDNS", "tcpReachable", "tlsSucceeded"], json.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
		Assert.Equal("notBlocked", json.GetProperty("kind").GetString());
	}
}
