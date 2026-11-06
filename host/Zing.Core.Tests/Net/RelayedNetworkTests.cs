using System.Net;
using Zing.Core.Net;

namespace Zing.Core.Tests.Net;

public sealed class RelayedNetworkTests
{
	private sealed class FixedDoh(params string[] addresses) : IDohClient
	{
		public Task<IReadOnlyList<string>> ResolveAsync(string host, string provider, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(addresses);
	}

	private static readonly IReadOnlyDictionary<string, string> Headers = new Dictionary<string, string> { ["User-Agent"] = "Zing" };

	private static RelayRequest To(string url) => new(new Uri(url), Headers);

	[Theory]
	[InlineData("127.0.0.1")]
	[InlineData("192.168.1.1")]
	[InlineData("10.1.2.3")]
	[InlineData("172.16.0.1")]
	[InlineData("169.254.169.254")]
	[InlineData("100.64.0.1")]
	[InlineData("100.127.255.255")]
	[InlineData("[::1]")]
	[InlineData("[fd00::1]")]
	[InlineData("localhost")]
	[InlineData("LOCALHOST.")]
	[InlineData("printer.local")]
	[InlineData("wiki.internal")]
	[InlineData("nas.lan")]
	[InlineData("intranet")]
	public void Own_network_hosts_are_refused(string host) => Assert.True(RelayedHosts.IsOwnNetworkHost(host));

	[Theory]
	[InlineData("example.com")]
	[InlineData("8.8.8.8")]
	[InlineData("100.63.255.255")]
	[InlineData("100.128.0.1")]
	[InlineData("web.archive.org")]
	public void Public_hosts_are_not(string host) => Assert.False(RelayedHosts.IsOwnNetworkHost(host));

	[Fact]
	public async Task A_loopback_server_is_refused_before_any_request_is_sent()
	{
		using var server = new PlainServer(_ => PlainServer.Ok("secret"));
		var transport = new LiveRelayTransport(new FixedDoh(), ConnectPolicy.PublicOnly);
		await Assert.ThrowsAsync<OwnNetworkError>(() => transport.GetAsync(To(server.To("/admin").AbsoluteUri), CancellationToken.None));
		Assert.Empty(server.Requests);
	}

	[Fact]
	public async Task A_name_that_resolves_to_a_private_address_is_refused_when_connecting()
	{
		using var server = new PlainServer(_ => PlainServer.Ok("secret"));
		// The name passes the text check on purpose: only the address it resolves to can give it away.
		var transport = new LiveRelayTransport(new FixedDoh(), new ConnectPolicy(_ => true, RelayedHosts.IsPublicAddress));
		var error = await Assert.ThrowsAsync<HttpRequestException>(() => transport.GetAsync(To($"http://localhost:{server.Port}/admin"), CancellationToken.None));
		Assert.IsType<OwnNetworkError>(error.InnerException);
		Assert.Empty(server.Requests);
	}

	[Fact]
	public async Task A_redirect_to_an_own_network_host_is_refused_at_that_hop()
	{
		using var server = new PlainServer(_ => PlainServer.Redirect("http://localhost:1/secret"));
		var transport = new LiveRelayTransport(new FixedDoh(), new ConnectPolicy(host => host != "localhost", _ => true));
		await Assert.ThrowsAsync<OwnNetworkError>(() => transport.GetAsync(To(server.To("/start").AbsoluteUri), CancellationToken.None));
		Assert.Equal(["/start"], server.Requests.Select(r => r.Split(' ')[1]));
	}

	[Fact]
	public async Task A_public_name_that_a_lookup_answers_with_a_private_address_is_not_connected_to()
	{
		var transport = new LiveRelayTransport(new FixedDoh("10.0.0.5"), ConnectPolicy.PublicOnly);
		await Assert.ThrowsAsync<OwnNetworkError>(() => transport.GetPinnedAsync(To("https://example.com/"), "cloudflare", CancellationToken.None));
	}

	[Fact]
	public void The_address_rule_covers_mapped_and_compatible_forms()
	{
		Assert.False(RelayedHosts.IsPublicAddress(IPAddress.Parse("::ffff:192.168.0.1")));
		Assert.False(RelayedHosts.IsPublicAddress(IPAddress.Parse("100.64.1.1")));
		Assert.True(RelayedHosts.IsPublicAddress(IPAddress.Parse("93.184.216.34")));
	}
}
