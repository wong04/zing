using Zing.Core.Net;

namespace Zing.Core.Tests.Net;

public sealed class LiveRelayTransportTests
{
	private sealed class FixedDoh(params string[] addresses) : IDohClient
	{
		public Task<IReadOnlyList<string>> ResolveAsync(string host, string provider, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(addresses);
	}

	private sealed class RecordingDoh(List<string> asked) : IDohClient
	{
		public Task<IReadOnlyList<string>> ResolveAsync(string host, string provider, CancellationToken ct)
		{
			asked.Add(host);
			return Task.FromResult<IReadOnlyList<string>>(["127.0.0.1"]);
		}
	}

	private static readonly IReadOnlyDictionary<string, string> Headers = new Dictionary<string, string> { ["User-Agent"] = "Zing", ["Accept"] = "text/html" };

	private static RelayRequest To(string url) => new(new Uri(url), Headers);

	[Fact]
	public async Task A_pinned_request_goes_to_the_looked_up_address_and_sends_only_the_headers_it_was_given()
	{
		using var pki = new TestPki();
		using var server = new TestServer(pki.IssueFor("example.test"));
		var transport = new LiveRelayTransport(new FixedDoh("127.0.0.1"), new PinnedHttp(pki.Trusted));
		var response = await transport.GetPinnedAsync(To($"https://example.test:{server.Port}/page"), "cloudflare", CancellationToken.None);
		Assert.Equal(200, response.Status);
		Assert.Equal("hi", System.Text.Encoding.UTF8.GetString(response.Body));
		var sent = server.LastRequest.ToLowerInvariant();
		Assert.Contains("user-agent: zing", sent);
		Assert.DoesNotContain("cookie", sent);
		Assert.DoesNotContain("authorization", sent);
	}

	[Fact]
	public async Task A_pinned_redirect_is_followed_with_its_own_lookup_and_pin()
	{
		using var pki = new TestPki();
		using var target = new TestServer(pki.IssueFor("other.test"));
		using var server = new TestServer(pki.IssueFor("example.test"), $"HTTP/1.1 302 Found\r\nLocation: https://other.test:{target.Port}/there\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
		var transport = new LiveRelayTransport(new FixedDoh("127.0.0.1"), new PinnedHttp(pki.Trusted));
		var response = await transport.GetPinnedAsync(To($"https://example.test:{server.Port}/"), "cloudflare", CancellationToken.None);
		Assert.Equal(200, response.Status);
		Assert.Equal($"https://other.test:{target.Port}/there", response.FinalUrl.AbsoluteUri);
	}

	[Fact]
	public async Task A_pinned_redirect_to_a_name_on_the_own_network_is_not_looked_up()
	{
		using var pki = new TestPki();
		using var server = new TestServer(pki.IssueFor("example.test"), "HTTP/1.1 302 Found\r\nLocation: https://nas.local/there\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
		var asked = new List<string>();
		var transport = new LiveRelayTransport(new RecordingDoh(asked), new PinnedHttp(pki.Trusted));
		await Assert.ThrowsAsync<OwnNetworkError>(() => transport.GetPinnedAsync(To($"https://example.test:{server.Port}/"), "cloudflare", CancellationToken.None));
		Assert.Equal(["example.test"], asked);
	}

	[Fact]
	public async Task A_pinned_redirect_loop_is_an_error()
	{
		using var pki = new TestPki();
		using var server = new TestServer(pki.IssueFor("example.test"), "HTTP/1.1 302 Found\r\nLocation: /again\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
		var transport = new LiveRelayTransport(new FixedDoh("127.0.0.1"), new PinnedHttp(pki.Trusted));
		await Assert.ThrowsAsync<TooManyRedirectsError>(() => transport.GetPinnedAsync(To($"https://example.test:{server.Port}/"), "cloudflare", CancellationToken.None));
	}

	[Fact]
	public async Task A_pinned_request_refuses_a_certificate_for_another_name()
	{
		using var pki = new TestPki();
		using var server = new TestServer(pki.IssueFor("other.test"));
		var transport = new LiveRelayTransport(new FixedDoh("127.0.0.1"), new PinnedHttp(pki.Trusted));
		await Assert.ThrowsAsync<System.Security.Authentication.AuthenticationException>(() => transport.GetPinnedAsync(To($"https://example.test:{server.Port}/"), "cloudflare", CancellationToken.None));
	}

	[Fact]
	public async Task A_pinned_request_for_plain_http_is_refused()
	{
		var transport = new LiveRelayTransport(new FixedDoh("127.0.0.1"), new PinnedHttp());
		await Assert.ThrowsAsync<NotHttpsError>(() => transport.GetPinnedAsync(To("http://example.test/"), "cloudflare", CancellationToken.None));
	}

	[Fact]
	public async Task A_lookup_with_no_address_is_an_error()
	{
		var transport = new LiveRelayTransport(new FixedDoh(), new PinnedHttp());
		await Assert.ThrowsAsync<NoAddressError>(() => transport.GetPinnedAsync(To("https://example.test/"), "cloudflare", CancellationToken.None));
	}
}
