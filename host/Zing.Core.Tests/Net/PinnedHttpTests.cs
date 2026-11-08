using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using Zing.Core.Net;

namespace Zing.Core.Tests.Net;

// Changes the process-wide default proxy for one test, so no other class may use HttpClient's default at the same time.
[Collection("ProcessWide")]
public sealed class PinnedHttpTests
{
	[Fact]
	public async Task Connects_to_the_pinned_address_but_checks_the_certificate_for_the_real_name()
	{
		using var pki = new TestPki();
		using var server = new TestServer(pki.IssueFor("example.test"));
		using var response = await new PinnedHttp(pki.Trusted).GetAsync(new Uri($"https://example.test:{server.Port}/page"), IPAddress.Loopback, CancellationToken.None);
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Assert.Equal("hi", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
	}

	[Fact]
	public async Task Refuses_a_certificate_for_another_name()
	{
		using var pki = new TestPki();
		using var server = new TestServer(pki.IssueFor("other.test"));
		await Assert.ThrowsAsync<AuthenticationException>(() =>
			new PinnedHttp(pki.Trusted).GetAsync(new Uri($"https://example.test:{server.Port}/"), IPAddress.Loopback, CancellationToken.None));
	}

	[Fact]
	public async Task Refuses_a_certificate_nobody_trusts()
	{
		using var server = new TestServer(TestPki.SelfSigned("example.test"));
		await Assert.ThrowsAsync<AuthenticationException>(() =>
			new PinnedHttp().GetAsync(new Uri($"https://example.test:{server.Port}/"), IPAddress.Loopback, CancellationToken.None));
	}

	[Fact]
	public async Task Plain_http_is_refused_so_a_pin_cannot_send_a_page_in_the_clear() =>
		await Assert.ThrowsAsync<ArgumentException>(() => new PinnedHttp().GetAsync(new Uri("http://example.test/"), IPAddress.Loopback, CancellationToken.None));

	[Fact]
	public async Task A_redirect_is_returned_not_followed_because_it_may_name_another_host()
	{
		using var pki = new TestPki();
		using var server = new TestServer(pki.IssueFor("example.test"), "HTTP/1.1 302 Found\r\nLocation: https://elsewhere.test/\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
		using var response = await new PinnedHttp(pki.Trusted).GetAsync(new Uri($"https://example.test:{server.Port}/"), IPAddress.Loopback, CancellationToken.None);
		Assert.Equal(HttpStatusCode.Found, response.StatusCode);
		Assert.Equal("https://elsewhere.test/", response.Headers.Location!.ToString());
	}

	[Fact]
	public async Task Does_not_go_through_the_system_proxy_because_the_dial_would_then_aim_at_the_proxy()
	{
		using var pki = new TestPki();
		using var server = new TestServer(pki.IssueFor("example.test"));
		var listener = new TcpListener(IPAddress.Loopback, 0);
		listener.Start();
		var deadProxyPort = ((IPEndPoint)listener.LocalEndpoint).Port;
		listener.Stop();
		var before = HttpClient.DefaultProxy;
		HttpClient.DefaultProxy = new WebProxy($"http://127.0.0.1:{deadProxyPort}");
		try
		{
			using var response = await new PinnedHttp(pki.Trusted).GetAsync(new Uri($"https://example.test:{server.Port}/"), IPAddress.Loopback, CancellationToken.None);
			Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		}
		finally
		{
			HttpClient.DefaultProxy = before;
		}
	}
}
