using System.Text;
using Zing.Core.Net;
using static Zing.Core.Tests.Net.RelayEngineTests;

namespace Zing.Core.Tests.Net;

public sealed class RelayedSiteTests
{
	private static RelayedSite SiteFor(FakeTransport transport, RelayConsent? consent = null) =>
		new(transport, () => "cloudflare", consent ?? new RelayConsent());

	private static string Text(RelayedResponse response) => Encoding.UTF8.GetString(response.Body);

	[Fact]
	public async Task A_document_is_served_patched_under_a_policy_that_keeps_it_in_the_relay()
	{
		var transport = new FakeTransport { Plain = _ => Ok("<img src=\"https://cdn.example.com/a.png\">", "text/html; charset=utf-8", url: "https://example.com/p") };
		var response = await SiteFor(transport).ServeAsync("GET", "zing-relay://example.com/p", isDocument: true, CancellationToken.None);
		Assert.Equal(200, response.Status);
		Assert.Equal("<img src=\"zing-relay://cdn.example.com/a.png\">", Text(response));
		Assert.Equal("text/html; charset=utf-8", response.Headers["Content-Type"]);
		Assert.Contains("connect-src zing-relay:", response.Headers["Content-Security-Policy"]);
		Assert.DoesNotContain("https:", response.Headers["Content-Security-Policy"]);
		Assert.Equal("no-store", response.Headers["Cache-Control"]);
	}

	[Fact]
	public async Task A_request_to_the_site_carries_no_cookie_or_sign_in()
	{
		var transport = new FakeTransport { Plain = _ => Ok("body", "text/css", url: "https://example.com/s.css") };
		await SiteFor(transport).ServeAsync("GET", "zing-relay://example.com/s.css", isDocument: false, CancellationToken.None);
		var headers = Assert.Single(transport.Calls).Request.Headers;
		Assert.DoesNotContain(headers.Keys, k => k.Equals("Cookie", StringComparison.OrdinalIgnoreCase) || k.Equals("Authorization", StringComparison.OrdinalIgnoreCase));
	}

	[Theory]
	[InlineData("POST")]
	[InlineData("PUT")]
	public async Task Only_GET_is_relayed(string method)
	{
		var transport = new FakeTransport();
		var response = await SiteFor(transport).ServeAsync(method, "zing-relay://example.com/login", isDocument: true, CancellationToken.None);
		Assert.Equal(405, response.Status);
		Assert.Empty(transport.Calls);
	}

	[Fact]
	public async Task An_address_that_is_not_in_the_relay_scheme_is_refused()
	{
		var transport = new FakeTransport();
		var response = await SiteFor(transport).ServeAsync("GET", "http://example.com/", isDocument: true, CancellationToken.None);
		Assert.Equal(400, response.Status);
		Assert.Empty(transport.Calls);
	}

	[Fact]
	public async Task A_page_that_ended_elsewhere_is_redirected_there_so_its_relative_links_resolve()
	{
		var transport = new FakeTransport { Plain = _ => Ok("x", "text/html", url: "https://www.example.com/en/") };
		var response = await SiteFor(transport).ServeAsync("GET", "zing-relay://example.com/", isDocument: true, CancellationToken.None);
		Assert.Equal(302, response.Status);
		Assert.Equal("zing-relay://www.example.com/en/", response.Headers["Location"]);
	}

	[Fact]
	public async Task Without_consent_a_blocked_page_says_it_needs_an_OK_and_no_third_party_is_asked()
	{
		var transport = new FakeTransport();
		var response = await SiteFor(transport).ServeAsync("GET", "zing-relay://example.com/", isDocument: true, CancellationToken.None);
		Assert.Equal(403, response.Status);
		Assert.Contains("OK", Text(response));
		Assert.DoesNotContain(transport.Calls, c => c.Request.Url.Host is "r.jina.ai" or "archive.org");
	}

	[Fact]
	public async Task A_subresource_of_the_own_network_never_goes_to_a_third_party_even_with_consent()
	{
		var consent = new RelayConsent();
		consent.Allow();
		var transport = new FakeTransport();
		var response = await SiteFor(transport, consent).ServeAsync("GET", "zing-relay://intranet/s.css", isDocument: false, CancellationToken.None);
		Assert.Equal(502, response.Status);
		Assert.DoesNotContain(transport.Calls, c => c.Request.Url.Host is "r.jina.ai" or "archive.org" or "web.archive.org");
	}

	[Fact]
	public async Task A_failure_is_shown_with_its_reasons_escaped()
	{
		var transport = new FakeTransport { Plain = _ => throw new HttpRequestException("<script>x</script>") };
		var consent = new RelayConsent();
		var response = await SiteFor(transport, consent).ServeAsync("GET", "zing-relay://example.com/", isDocument: true, CancellationToken.None);
		Assert.Equal(403, response.Status);
		var failed = await SiteFor(new FakeTransport { Plain = _ => throw new HttpRequestException("<b>boom</b>"), Pinned = _ => throw new HttpRequestException("<b>boom</b>") }, AllowedConsent())
			.ServeAsync("GET", "zing-relay://example.com/", isDocument: true, CancellationToken.None);
		Assert.Equal(502, failed.Status);
		Assert.Contains("&lt;b&gt;boom&lt;/b&gt;", Text(failed));
		Assert.DoesNotContain("<b>boom", Text(failed));
	}

	private static RelayConsent AllowedConsent()
	{
		var consent = new RelayConsent();
		consent.Allow();
		return consent;
	}
}
