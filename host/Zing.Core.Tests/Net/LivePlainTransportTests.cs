using Zing.Core.Net;

namespace Zing.Core.Tests.Net;

/// <summary>The ordinary (system DNS) request of <see cref="LiveRelayTransport"/>, against a server on this machine.</summary>
// The plain request uses the process-wide default proxy, which PinnedHttpTests changes for one test.
[Collection("HttpDefaultProxy")]
public sealed class LivePlainTransportTests
{
	private sealed class NoDoh : IDohClient
	{
		public Task<IReadOnlyList<string>> ResolveAsync(string host, string provider, CancellationToken ct) => throw new InvalidOperationException("not used");
	}

	private static readonly IReadOnlyDictionary<string, string> Headers = new Dictionary<string, string> { ["User-Agent"] = "Zing", ["Accept"] = "text/html" };

	private static LiveRelayTransport Transport() => new(new NoDoh());

	private static RelayRequest To(Uri url) => new(url, Headers);

	private static readonly byte[] Big = new byte[(int)PinnedHttp.MaxBodyBytes + 1];

	[Fact]
	public async Task A_page_comes_back_with_its_status_and_type()
	{
		using var server = new PlainServer(_ => PlainServer.Ok("hello"));
		var response = await Transport().GetAsync(To(server.To("/page")), CancellationToken.None);
		Assert.Equal(200, response.Status);
		Assert.Equal("hello", System.Text.Encoding.UTF8.GetString(response.Body));
		Assert.StartsWith("text/html", response.ContentType);
	}

	[Fact]
	public async Task Only_the_given_headers_are_sent_no_cookie_and_no_credentials()
	{
		using var server = new PlainServer(_ => PlainServer.Ok("hello"));
		await Transport().GetAsync(To(server.To("/page")), CancellationToken.None);
		var sent = server.Requests.Single().ToLowerInvariant();
		Assert.Contains("user-agent: zing", sent);
		Assert.DoesNotContain("cookie", sent);
		Assert.DoesNotContain("authorization", sent);
	}

	[Fact]
	public async Task A_followed_redirect_reports_where_it_ended()
	{
		using var server = new PlainServer(path => path == "/a" ? PlainServer.Redirect("/b") : PlainServer.Ok("there"));
		var response = await Transport().GetAsync(To(server.To("/a")), CancellationToken.None);
		Assert.Equal(200, response.Status);
		Assert.Equal(server.To("/b"), response.FinalUrl);
	}

	[Fact]
	public async Task A_redirect_loop_is_an_error_not_a_page()
	{
		using var server = new PlainServer(_ => PlainServer.Redirect("/again"));
		await Assert.ThrowsAsync<TooManyRedirectsError>(() => Transport().GetAsync(To(server.To("/again")), CancellationToken.None));
	}

	[Fact]
	public async Task A_redirect_with_nowhere_to_go_is_an_error()
	{
		using var server = new PlainServer(_ => PlainServer.Response("302 Found", "", []));
		await Assert.ThrowsAsync<BadRedirectError>(() => Transport().GetAsync(To(server.To("/")), CancellationToken.None));
	}

	[Fact]
	public async Task A_redirect_to_ftp_is_a_named_error()
	{
		using var server = new PlainServer(_ => PlainServer.Redirect("ftp://example.test/x"));
		await Assert.ThrowsAsync<RedirectNotFollowedError>(() => Transport().GetAsync(To(server.To("/")), CancellationToken.None));
	}

	[Fact]
	public async Task A_redirect_to_ftp_fails_that_attempt_and_the_engine_moves_on()
	{
		using var server = new PlainServer(_ => PlainServer.Redirect("ftp://example.test/x"));
		var seen = new List<RelayAttempt>();
		var engine = new RelayEngine(Transport(), "cloudflare", seen.Add);
		var error = await Assert.ThrowsAsync<AllStrategiesFailedError>(() => engine.OpenAsync(server.To("/").AbsoluteUri, new RelayConsent(), CancellationToken.None));
		Assert.Equal(["Direct:Trying", "Direct:Failed", "DohPinned:Trying", "DohPinned:Failed", "ReaderRelay:Skipped", "Wayback:Skipped"], seen.Select(a => $"{a.Strategy}:{a.Status}"));
		Assert.Contains("redirected somewhere Zing wouldn't follow", seen[1].Reason);
		Assert.NotEmpty(error.Attempts);
	}

	[Fact]
	public async Task A_redirect_to_an_address_that_is_not_one_is_a_named_error()
	{
		using var server = new PlainServer(_ => PlainServer.Redirect("http://[bad/"));
		await Assert.ThrowsAsync<BadRedirectError>(() => Transport().GetAsync(To(server.To("/")), CancellationToken.None));
	}

	[Fact]
	public async Task A_reply_that_is_not_http_fails_the_attempt_and_the_engine_moves_on()
	{
		using var server = new PlainServer(_ => System.Text.Encoding.ASCII.GetBytes("this is not http\r\n\r\n"));
		var seen = new List<RelayAttempt>();
		var engine = new RelayEngine(Transport(), "cloudflare", seen.Add);
		await Assert.ThrowsAsync<AllStrategiesFailedError>(() => engine.OpenAsync(server.To("/").AbsoluteUri, new RelayConsent(), CancellationToken.None));
		Assert.Equal(AttemptStatus.Failed, seen[1].Status);
		Assert.Equal(AttemptStatus.Trying, seen[2].Status);
	}

	[Fact]
	public async Task A_body_past_the_limit_is_a_BodyTooLargeError()
	{
		using var server = new PlainServer(_ => PlainServer.Response("200 OK", "Content-Type: text/html\r\n", Big));
		var error = await Assert.ThrowsAsync<BodyTooLargeError>(() => Transport().GetAsync(To(server.To("/")), CancellationToken.None));
		Assert.Contains("16 MiB", error.Message);
	}

	[Fact]
	public async Task A_pinned_body_past_the_limit_is_a_BodyTooLargeError()
	{
		using var pki = new TestPki();
		using var server = new TestServer(pki.IssueFor("example.test"), $"HTTP/1.1 200 OK\r\nContent-Length: {Big.Length}\r\nConnection: close\r\n\r\n" + new string('a', Big.Length));
		var transport = new LiveRelayTransport(new FixedDoh(), new PinnedHttp(pki.Trusted));
		await Assert.ThrowsAsync<BodyTooLargeError>(() => transport.GetPinnedAsync(To(new Uri($"https://example.test:{server.Port}/")), "cloudflare", CancellationToken.None));
	}

	[Fact]
	public async Task A_pinned_3xx_that_is_not_a_redirect_is_an_error_not_a_page()
	{
		using var pki = new TestPki();
		using var server = new TestServer(pki.IssueFor("example.test"), "HTTP/1.1 304 Not Modified\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
		var transport = new LiveRelayTransport(new FixedDoh(), new PinnedHttp(pki.Trusted));
		await Assert.ThrowsAsync<RedirectNotFollowedError>(() => transport.GetPinnedAsync(To(new Uri($"https://example.test:{server.Port}/")), "cloudflare", CancellationToken.None));
	}

	private sealed class FixedDoh : IDohClient
	{
		public Task<IReadOnlyList<string>> ResolveAsync(string host, string provider, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(["127.0.0.1"]);
	}
}
