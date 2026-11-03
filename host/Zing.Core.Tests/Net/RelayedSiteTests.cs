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
	public async Task A_final_address_the_relay_cannot_stand_for_is_an_error_not_a_silent_serve()
	{
		var transport = new FakeTransport { Plain = _ => Ok("x", "text/html", url: "http://example.com/downgraded") };
		var response = await SiteFor(transport).ServeAsync("GET", "zing-relay://example.com/", isDocument: true, CancellationToken.None);
		Assert.Equal(502, response.Status);
		Assert.Contains("https", Text(response));
	}

	[Fact]
	public void Fetching_and_patching_do_not_run_on_the_calling_thread_which_is_the_window_s()
	{
		// A thread of its own, standing for the window's: a pool thread could pick the work up itself and be the same one.
		var caller = -1;
		var transportThread = -1;
		var transport = new FakeTransport { Plain = _ => { transportThread = Environment.CurrentManagedThreadId; return Ok("x", "text/html", url: "https://example.com/"); } };
		var window = new Thread(() =>
		{
			caller = Environment.CurrentManagedThreadId;
			SiteFor(transport).ServeAsync("GET", "zing-relay://example.com/", isDocument: true, CancellationToken.None).GetAwaiter().GetResult();
		});
		window.Start();
		window.Join();
		Assert.NotEqual(-1, transportThread);
		Assert.NotEqual(caller, transportThread);
	}

	[Fact]
	public async Task Responses_may_not_be_sniffed_into_another_type()
	{
		var transport = new FakeTransport { Plain = _ => Ok("body", "text/plain", url: "https://example.com/s") };
		var response = await SiteFor(transport).ServeAsync("GET", "zing-relay://example.com/s", isDocument: false, CancellationToken.None);
		Assert.Equal("nosniff", response.Headers["X-Content-Type-Options"]);
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

	[Theory]
	[InlineData("zing-relay://127.0.0.1/admin")]
	[InlineData("zing-relay://localhost/")]
	[InlineData("zing-relay://192.168.1.1/admin")]
	[InlineData("zing-relay://100.64.0.9/")]
	[InlineData("zing-relay://nas.lan/x")]
	[InlineData("zing-relay://intranet/s.css")]
	public async Task The_own_network_is_refused_for_a_document_or_a_file_before_any_fetch(string url)
	{
		var consent = new RelayConsent();
		consent.Allow();
		foreach (var isDocument in new[] { true, false })
		{
			var transport = new FakeTransport();
			var response = await SiteFor(transport, consent).ServeAsync("GET", url, isDocument, CancellationToken.None);
			Assert.Equal(403, response.Status);
			Assert.Empty(transport.Calls);
		}
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
