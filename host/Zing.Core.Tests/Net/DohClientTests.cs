using System.Net;
using System.Text;
using Zing.Core.Net;

namespace Zing.Core.Tests.Net;

[Collection("HttpDefaultProxy")]
public sealed class DohClientTests
{
	private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
	{
		public List<HttpRequestMessage> Requests { get; } = [];

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			Requests.Add(request);
			return Task.FromResult(reply(request));
		}
	}

	private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
		new(status) { Content = new StringContent(body, Encoding.UTF8, "application/dns-json") };

	private static (DohClient Client, StubHandler Handler) ClientAnswering(string body, HttpStatusCode status = HttpStatusCode.OK)
	{
		var handler = new StubHandler(_ => Json(body, status));
		return (new DohClient(new HttpClient(handler)), handler);
	}

	[Fact]
	public async Task Reads_the_A_records_and_skips_CNAMEs()
	{
		var (client, _) = ClientAnswering("""{"Status":0,"Answer":[{"name":"www.example.com","type":5,"data":"example.com."},{"name":"example.com","type":1,"data":"93.184.216.34"}]}""");
		Assert.Equal(["93.184.216.34"], await client.ResolveAsync("www.example.com", "cloudflare", CancellationToken.None));
	}

	[Fact]
	public async Task A_name_with_no_address_records_is_an_empty_answer()
	{
		var (client, _) = ClientAnswering("""{"Status":0}""");
		Assert.Empty(await client.ResolveAsync("example.com", "google", CancellationToken.None));
	}

	[Fact]
	public async Task Asks_Cloudflare_for_the_JSON_dialect()
	{
		var (client, handler) = ClientAnswering("""{"Status":0}""");
		await client.ResolveAsync("example.com", "cloudflare", CancellationToken.None);
		var request = Assert.Single(handler.Requests);
		Assert.Equal("cloudflare-dns.com", request.RequestUri!.Host);
		Assert.Equal("name=example.com&type=A", request.RequestUri.Query.TrimStart('?'));
		Assert.Contains("application/dns-json", request.Headers.Accept.Select(a => a.MediaType));
	}

	[Theory]
	[InlineData("cloudflare", "cloudflare-dns.com")]
	[InlineData("cloudflare-security", "security.cloudflare-dns.com")]
	[InlineData("google", "dns.google")]
	[InlineData("quad9", "dns.quad9.net")]
	public async Task Each_setting_value_names_its_resolver(string provider, string host)
	{
		var (client, handler) = ClientAnswering("""{"Status":0}""");
		await client.ResolveAsync("example.com", provider, CancellationToken.None);
		Assert.Equal(host, Assert.Single(handler.Requests).RequestUri!.Host);
	}

	[Fact]
	public async Task An_unknown_provider_is_refused() =>
		await Assert.ThrowsAsync<UnknownDohProviderError>(() => ClientAnswering("{}").Client.ResolveAsync("example.com", "nope", CancellationToken.None));

	[Fact]
	public async Task Status_3_is_NXDOMAIN()
	{
		var (client, _) = ClientAnswering("""{"Status":3}""");
		var error = await Assert.ThrowsAsync<NxDomainError>(() => client.ResolveAsync("nope.example", "cloudflare", CancellationToken.None));
		Assert.Contains("nope.example", error.Message);
	}

	[Fact]
	public async Task Another_status_is_a_resolver_status_error()
	{
		var (client, _) = ClientAnswering("""{"Status":2}""");
		var error = await Assert.ThrowsAsync<ResolverStatusError>(() => client.ResolveAsync("example.com", "cloudflare", CancellationToken.None));
		Assert.Equal(2, error.Status);
	}

	[Theory]
	[InlineData("not json")]
	[InlineData("[]")]
	[InlineData("""{"Answer":[]}""")]
	[InlineData("""{"Status":0,"Answer":"x"}""")]
	[InlineData("""{"Status":0,"Answer":[{"name":"a","type":1}]}""")]
	[InlineData("""{"Status":0,"Answer":[{"name":"a","type":1,"data":"not-an-ip"}]}""")]
	[InlineData("""{"Status":0,"Answer":[{"name":"a","type":1,"data":"10"}]}""")]
	[InlineData("""{"Status":0,"Answer":[{"name":"a","type":1,"data":"2606:4700::1111"}]}""")]
	public async Task Bad_JSON_is_a_MalformedDoHResponseError(string body)
	{
		var (client, _) = ClientAnswering(body);
		await Assert.ThrowsAsync<MalformedDoHResponseError>(() => client.ResolveAsync("example.com", "cloudflare", CancellationToken.None));
	}

	[Fact]
	public async Task An_HTTP_error_means_the_resolver_cannot_be_used()
	{
		var (client, _) = ClientAnswering("blocked", HttpStatusCode.Forbidden);
		var error = await Assert.ThrowsAsync<ResolverUnreachableError>(() => client.ResolveAsync("example.com", "cloudflare", CancellationToken.None));
		Assert.Contains("403", error.Message);
	}

	[Fact]
	public async Task A_connection_failure_means_the_resolver_cannot_be_used()
	{
		var client = new DohClient(new HttpClient(new StubHandler(_ => throw new HttpRequestException("connection reset"))));
		var error = await Assert.ThrowsAsync<ResolverUnreachableError>(() => client.ResolveAsync("example.com", "cloudflare", CancellationToken.None));
		Assert.Contains("connection reset", error.Message);
	}

	[Fact]
	public async Task A_cancelled_lookup_is_cancelled_not_unreachable()
	{
		var (client, _) = ClientAnswering("""{"Status":0}""");
		using var cancel = new CancellationTokenSource();
		await cancel.CancelAsync();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ResolveAsync("example.com", "cloudflare", cancel.Token));
	}

	[Fact]
	public async Task Keeps_no_cookies_between_lookups()
	{
		var cookies = new List<string?>();
		using var listener = new HttpListener();
		var prefix = $"http://127.0.0.1:{FreePort()}/";
		listener.Prefixes.Add(prefix);
		listener.Start();
		var serving = Task.Run(async () =>
		{
			for (var i = 0; i < 2; i++)
			{
				var context = await listener.GetContextAsync();
				cookies.Add(context.Request.Headers["Cookie"]);
				context.Response.AppendHeader("Set-Cookie", "a=b; Path=/");
				await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("""{"Status":0}"""));
				context.Response.Close();
			}
		}, TestContext.Current.CancellationToken);
		var client = new DohClient(DohClient.NewHttpClient(), _ => prefix);
		await client.ResolveAsync("example.com", "cloudflare", TestContext.Current.CancellationToken);
		await client.ResolveAsync("example.com", "cloudflare", TestContext.Current.CancellationToken);
		await serving;
		Assert.Equal([null, null], cookies);
	}

	private static int FreePort()
	{
		var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
		listener.Start();
		var port = ((IPEndPoint)listener.LocalEndpoint).Port;
		listener.Stop();
		return port;
	}
}
