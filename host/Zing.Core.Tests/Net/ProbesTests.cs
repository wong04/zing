using System.Net;
using System.Net.Sockets;
using Zing.Core.Net;

namespace Zing.Core.Tests.Net;

public sealed class ProbesTests
{
	private static int ClosedPort()
	{
		var listener = new TcpListener(IPAddress.Loopback, 0);
		listener.Start();
		var port = ((IPEndPoint)listener.LocalEndpoint).Port;
		listener.Stop();
		return port;
	}

	[Fact]
	public async Task The_system_resolver_answers_for_localhost()
	{
		var answers = await new Probes().ResolveSystemAsync("localhost", CancellationToken.None);
		Assert.Contains("127.0.0.1", answers);
	}

	[Fact]
	public async Task Tcp_is_reachable_when_something_listens()
	{
		using var server = new TestServer();
		Assert.True(await new Probes(server.Port).TcpAsync("127.0.0.1", CancellationToken.None));
	}

	[Fact]
	public async Task Tcp_is_not_reachable_when_the_port_refuses() =>
		Assert.False(await new Probes(ClosedPort()).TcpAsync("127.0.0.1", CancellationToken.None));

	[Fact]
	public async Task Tcp_is_not_reachable_when_nothing_answers_before_the_deadline() =>
		// 192.0.2.0/24 is reserved for documentation, so a connect there goes nowhere.
		Assert.False(await new Probes(443, TimeSpan.FromMilliseconds(300)).TcpAsync("192.0.2.1", CancellationToken.None));

	[Fact]
	public async Task A_cancelled_probe_is_cancelled_not_unreachable()
	{
		using var cancel = new CancellationTokenSource();
		await cancel.CancelAsync();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new Probes(443, TimeSpan.FromSeconds(5)).TcpAsync("192.0.2.1", cancel.Token));
	}

	[Fact]
	public async Task A_certificate_for_the_host_from_a_trusted_root_is_valid()
	{
		using var pki = new TestPki();
		using var server = new TestServer(pki.IssueFor("example.test"));
		var result = await new Probes(server.Port, trustedRoots: pki.Trusted).TlsAsync("127.0.0.1", "example.test", CancellationToken.None);
		Assert.True(result.HandshakeCompleted);
		Assert.True(result.CertificateValid);
	}

	[Fact]
	public async Task A_certificate_for_another_name_completes_the_handshake_but_is_not_valid()
	{
		using var pki = new TestPki();
		using var server = new TestServer(pki.IssueFor("other.test"));
		var result = await new Probes(server.Port, trustedRoots: pki.Trusted).TlsAsync("127.0.0.1", "example.test", CancellationToken.None);
		Assert.True(result.HandshakeCompleted);
		Assert.False(result.CertificateValid);
		Assert.Contains("RemoteCertificateNameMismatch", result.Failure);
	}

	[Fact]
	public async Task An_untrusted_certificate_completes_the_handshake_but_is_not_valid()
	{
		using var server = new TestServer(TestPki.SelfSigned("example.test"));
		var result = await new Probes(server.Port).TlsAsync("127.0.0.1", "example.test", CancellationToken.None);
		Assert.True(result.HandshakeCompleted);
		Assert.False(result.CertificateValid);
	}

	[Fact]
	public async Task A_server_that_hangs_up_during_the_handshake_has_no_handshake()
	{
		using var server = new TestServer();
		var result = await new Probes(server.Port).TlsAsync("127.0.0.1", "example.test", CancellationToken.None);
		Assert.False(result.HandshakeCompleted);
		Assert.False(result.CertificateValid);
		Assert.NotNull(result.Failure);
	}
}
