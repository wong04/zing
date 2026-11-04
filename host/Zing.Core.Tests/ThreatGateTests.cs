using System.Net;
using System.Text.RegularExpressions;

namespace Zing.Core.Tests;

public sealed partial class ThreatGateTests
{
	private const string Flagged = "https://apple.com.secure-login.tk/verify";
	private const string Clean = "https://github.com/";
	private const string Page = "https://news.example.org/story";

	private bool _on = true;
	private readonly ThreatGate _gate;

	public ThreatGateTests()
	{
		_gate = new ThreatGate(() => _on);
	}

	private static string WarningUrlOf(NavigationDecision? decision) => Assert.IsType<NavigationDecision.Redirect>(decision).Url;

	private static string Html(StartPageResponse? response) => Assert.IsType<StartPageResponse>(response).Body;

	private StartPageResponse? Serve(string url) => _gate.Serve(url, new SettingsStore().Snapshot());

	[GeneratedRegex("href=\"(zing://visit-anyway[^\"]*)\"")]
	private static partial Regex VisitLink();

	/// <summary>The warning page for <paramref name="to"/> and the link a person would click on it.</summary>
	private (string Warning, string Visit) Warn(string to, string from = Page)
	{
		var warning = WarningUrlOf(_gate.Check(from, to));
		var match = VisitLink().Match(Html(Serve(warning)));
		Assert.True(match.Success, "the warning page has a Visit anyway link");
		return (warning, WebUtility.HtmlDecode(match.Groups[1].Value));
	}

	// MARK: - Deciding

	[Fact]
	public void A_clean_address_goes_through() => Assert.Null(_gate.Check(Page, Clean));

	[Fact]
	public void A_flagged_address_is_sent_to_the_warning_which_carries_it_encoded()
	{
		var warning = WarningUrlOf(_gate.Check(Page, Flagged));
		Assert.StartsWith("zing://warning?to=", warning);
		Assert.Equal(Flagged, StartPage.QueryParam(warning, "to"));
		Assert.DoesNotContain("://apple", warning);
	}

	[Theory]
	[InlineData("file:///C:/apple.com.secure-login.tk")]
	[InlineData("zing://home")]
	[InlineData("about:blank")]
	[InlineData("javascript:alert(1)")]
	[InlineData("not an address")]
	public void Only_web_addresses_are_inspected(string to) => Assert.Null(_gate.Check(Page, to));

	[Fact]
	public void Turning_the_setting_off_takes_effect_on_the_next_navigation()
	{
		Assert.NotNull(_gate.Check(Page, Flagged));
		_on = false;
		Assert.Null(_gate.Check(Page, Flagged));
		_on = true;
		Assert.NotNull(_gate.Check(Page, Flagged));
	}

	[Fact]
	public void A_redirect_to_a_flagged_address_is_inspected_like_any_navigation()
	{
		// The engine reports a redirect as a navigation from the page that is still showing.
		Assert.NotNull(_gate.Check("https://short.example/abc", Flagged));
	}

	[Fact]
	public void A_notice_alone_does_not_warn() => Assert.Null(_gate.Check(Page, "https://185.243.115.24/"));

	[Fact]
	public void A_local_address_stands_in_for_a_flagged_one_only_when_the_gate_was_given_the_mapping()
	{
		var local = "http://127.0.0.1:5000/phish";
		Assert.Null(_gate.Check(Page, local));
		var mapped = new ThreatGate(() => true, uri => uri.Port == 5000 ? new UriBuilder(uri) { Host = "apple.com.secure-login.tk" }.Uri : uri);
		Assert.NotNull(mapped.Check(Page, local));
	}

	// MARK: - Relayed pages

	[Fact]
	public void A_relayed_load_is_inspected_on_the_address_it_stands_for()
	{
		Assert.Null(_gate.Check(Page, "zing-relay://github.com/"));
		var warning = WarningUrlOf(_gate.Check("zing://home", "zing-relay://apple.com.secure-login.tk/verify"));
		Assert.Equal(Flagged, StartPage.QueryParam(warning, "to"));
		Assert.Equal("relay", StartPage.QueryParam(warning, "via"));
	}

	[Fact]
	public void Visiting_a_flagged_relayed_address_anyway_goes_back_through_the_relay()
	{
		var (warning, visit) = Warn("zing-relay://apple.com.secure-login.tk/verify", "zing://home");
		var decision = _gate.Check(warning, visit);
		Assert.Equal("zing-relay://apple.com.secure-login.tk/verify", Assert.IsType<NavigationDecision.Redirect>(decision).Url);
	}

	[Fact]
	public void A_page_cannot_ask_for_the_relay_by_making_up_a_warning()
	{
		// Only the address the check itself sent to the warning page can come back through the relay.
		var (warning, visit) = Warn(Flagged);
		var crafted = visit + "&via=relay";
		Assert.Equal(Flagged, Assert.IsType<NavigationDecision.Redirect>(_gate.Check(warning, crafted)).Url);
	}

	// MARK: - Visit anyway

	[Fact]
	public void Visit_anyway_from_the_warning_loads_the_address_and_does_not_warn_again_for_the_host()
	{
		var (warning, visit) = Warn(Flagged);
		Assert.Equal(Flagged, Assert.IsType<NavigationDecision.Redirect>(_gate.Check(warning, visit)).Url);
		Assert.Null(_gate.Check(Page, Flagged));
		Assert.Null(_gate.Check(Page, "https://apple.com.secure-login.tk/other/page"));
		Assert.NotNull(_gate.Check(Page, "https://paypal.account-update.xyz/signin"));
	}

	[Fact]
	public void The_allowed_hosts_are_not_shared_with_another_session() =>
		Assert.NotNull(new ThreatGate(() => true).Check(Page, Flagged));

	[Theory]
	[InlineData("https://evil.example/page")]
	[InlineData("zing://home")]
	[InlineData("")]
	[InlineData("about:blank")]
	public void Visit_anyway_from_anywhere_but_the_warning_is_refused_and_allows_nothing(string from)
	{
		var (_, visit) = Warn(Flagged);
		Assert.IsType<NavigationDecision.Cancelled>(_gate.Check(from, visit));
		Assert.NotNull(_gate.Check(Page, Flagged));
	}

	[Fact]
	public void Visit_anyway_from_a_warning_for_another_address_is_refused()
	{
		var (_, visit) = Warn(Flagged);
		var otherWarning = WarningUrlOf(_gate.Check(Page, "https://paypal.account-update.xyz/signin"));
		Assert.IsType<NavigationDecision.Cancelled>(_gate.Check(otherWarning, visit));
		Assert.NotNull(_gate.Check(Page, Flagged));
	}

	[Fact]
	public void A_page_that_reaches_the_warning_window_cannot_use_it_without_the_key_the_warning_page_carries()
	{
		// A script that opened the window the warning is in can set its location; the key is on the page it cannot read.
		var (warning, _) = Warn(Flagged);
		var forged = "zing://visit-anyway?to=" + Uri.EscapeDataString(Flagged);
		Assert.IsType<NavigationDecision.Cancelled>(_gate.Check(warning, forged));
		Assert.IsType<NavigationDecision.Cancelled>(_gate.Check(warning, forged + "&k=0000"));
		Assert.NotNull(_gate.Check(Page, Flagged));
	}

	[Fact]
	public void A_key_works_for_the_address_it_was_issued_for_only()
	{
		var (warning, visit) = Warn(Flagged);
		var other = "https://paypal.account-update.xyz/signin";
		var swapped = Regex.Replace(visit, "to=[^&]*", "to=" + Uri.EscapeDataString(other));
		Assert.IsType<NavigationDecision.Cancelled>(_gate.Check(warning, swapped));
		Assert.IsType<NavigationDecision.Cancelled>(_gate.Check(WarningUrlOf(_gate.Check(Page, other)), swapped));
		Assert.NotNull(_gate.Check(Page, other));
	}

	[Fact]
	public void A_key_is_used_once()
	{
		var (warning, visit) = Warn(Flagged);
		Assert.IsType<NavigationDecision.Redirect>(_gate.Check(warning, visit));
		Assert.IsType<NavigationDecision.Cancelled>(_gate.Check(warning, visit));
	}

	[Fact]
	public void Reloading_the_warning_gives_a_working_link()
	{
		var (warning, _) = Warn(Flagged);
		var again = VisitLink().Match(Html(Serve(warning))).Groups[1].Value;
		Assert.IsType<NavigationDecision.Redirect>(_gate.Check(warning, WebUtility.HtmlDecode(again)));
	}

	[Theory]
	[InlineData("javascript:alert(1)")]
	[InlineData("file:///C:/Windows/win.ini")]
	[InlineData("ftp://example.com/")]
	[InlineData("zing://home")]
	[InlineData("zing-relay://example.com/")]
	[InlineData("")]
	public void Only_http_and_https_can_be_visited_anyway(string to)
	{
		var warning = "zing://warning?to=" + Uri.EscapeDataString(to);
		var visit = "zing://visit-anyway?to=" + Uri.EscapeDataString(to) + "&k=1";
		Assert.IsType<NavigationDecision.Cancelled>(_gate.Check(warning, visit));
	}

	[Fact]
	public void Visit_anyway_with_no_address_is_refused() =>
		Assert.IsType<NavigationDecision.Cancelled>(_gate.Check("zing://warning?to=x", "zing://visit-anyway"));

	[Fact]
	public void Visit_anyway_works_even_if_the_setting_has_since_been_turned_off()
	{
		var (warning, visit) = Warn(Flagged);
		_on = false;
		Assert.IsType<NavigationDecision.Redirect>(_gate.Check(warning, visit));
	}

	// MARK: - The warning page

	[Fact]
	public void The_page_names_the_host_plainly_with_the_reasons_and_says_it_is_a_guess_from_the_address()
	{
		var body = Html(Serve(WarningUrlOf(_gate.Check(Page, Flagged))));
		Assert.Contains("apple.com.secure-login.tk", body);
		Assert.Contains("Impersonates Apple", body);
		Assert.Contains("High-risk domain ending (.tk)", body);
		Assert.Contains("guess", body);
		Assert.Contains("nothing was sent", body, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public void Go_back_is_the_main_action_and_visit_anyway_the_smaller_one()
	{
		var body = Html(Serve(WarningUrlOf(_gate.Check(Page, Flagged))));
		Assert.True(body.IndexOf("Go back", StringComparison.Ordinal) is var back and > 0 && body.IndexOf("Visit anyway", StringComparison.Ordinal) > back);
		Assert.Contains("class=\"primary\"", body);
		Assert.Contains("class=\"minor\"", body);
	}

	[Fact]
	public void Go_back_does_not_loop_and_has_somewhere_to_go_with_no_history()
	{
		var body = Html(Serve(WarningUrlOf(_gate.Check(Page, Flagged))));
		Assert.Contains("history.length > 1", body);
		Assert.Contains("history.back()", body);
		Assert.Contains("location.replace('zing://home')", body);
	}

	[Theory]
	[InlineData("")]
	[InlineData("about:blank")]
	public void A_warning_with_no_page_behind_it_goes_to_the_start_page_and_never_back(string from)
	{
		var body = Html(Serve(WarningUrlOf(_gate.Check(from, Flagged))));
		Assert.Contains("location.replace('zing://home')", body);
		Assert.DoesNotContain("history.back()", body);
	}

	[Fact]
	public void The_address_is_text_on_the_page_and_never_markup()
	{
		var evil = "https://apple.com.secure-login.tk/<script>alert(1)</script>\"onmouseover=\"x'&to=https://nested.example/";
		var warning = WarningUrlOf(_gate.Check(Page, evil));
		var body = Html(Serve(warning));
		Assert.DoesNotContain("<script>alert", body);
		Assert.DoesNotContain("\"onmouseover=\"", body);
		Assert.Contains("&amp;to=https://nested.example/", body);
		// The nested to= stays inside the one encoded value, and comes back whole.
		var visit = WebUtility.HtmlDecode(VisitLink().Match(body).Groups[1].Value);
		Assert.Equal(new Uri(evil).AbsoluteUri, StartPage.QueryParam(visit, "to"));
		Assert.Equal(new Uri(evil).AbsoluteUri, StartPage.QueryParam(warning, "to"));
		Assert.IsType<NavigationDecision.Redirect>(_gate.Check(warning, visit));
		Assert.Null(_gate.Check(Page, "https://nested.example/"));
	}

	[Fact]
	public void A_crafted_warning_address_is_escaped_as_it_arrives()
	{
		// Not the form the check itself produces (that one is percent-encoded already): whatever a page puts in the query.
		var crafted = "https://apple.com.secure-login.tk/<script>alert(1)</script>\"><img src=x onerror=alert(2)>";
		var body = Html(Serve("zing://warning?to=" + Uri.EscapeDataString(crafted)));
		Assert.DoesNotContain("<script>alert", body);
		Assert.DoesNotContain("<img", body);
		Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;&quot;&gt;&lt;img src=x onerror=alert(2)&gt;", body);
	}

	[Theory]
	[InlineData("javascript:alert(document.domain)")]
	[InlineData("file:///C:/Windows/win.ini")]
	[InlineData("zing://home")]
	[InlineData("not a url")]
	public void The_page_refuses_an_address_that_is_not_http_or_https_and_links_nothing(string to)
	{
		var response = Assert.IsType<StartPageResponse>(Serve("zing://warning?to=" + Uri.EscapeDataString(to)));
		Assert.Equal(400, response.Status);
		Assert.DoesNotContain("visit-anyway", response.Body);
		Assert.DoesNotContain("href=", response.Body);
	}

	[Fact]
	public void The_page_has_nothing_to_say_about_an_address_the_check_finds_fine()
	{
		var response = Assert.IsType<StartPageResponse>(Serve("zing://warning?to=" + Uri.EscapeDataString(Clean)));
		Assert.Equal(400, response.Status);
		Assert.DoesNotContain("visit-anyway", response.Body);
	}

	[Fact]
	public void The_page_is_never_cached()
	{
		var response = Assert.IsType<StartPageResponse>(Serve(WarningUrlOf(_gate.Check(Page, Flagged))));
		Assert.Equal("no-store", response.CacheControl);
		Assert.Equal(200, response.Status);
	}

	[Fact]
	public void Other_zing_addresses_are_not_the_gates_to_serve()
	{
		Assert.Null(Serve("zing://home"));
		Assert.Null(Serve("zing://visit-anyway?to=" + Uri.EscapeDataString(Flagged)));
	}

	[Fact]
	public void A_page_that_only_asks_for_warnings_cannot_grow_the_table_without_bound()
	{
		for (var i = 0; i < 500; i++) Serve("zing://warning?to=" + Uri.EscapeDataString($"https://apple.com.secure-login.tk/{i}"));
		var (warning, visit) = Warn(Flagged);
		Assert.IsType<NavigationDecision.Redirect>(_gate.Check(warning, visit));
		Assert.True(_gate.KeysHeld <= ThreatGate.KeysKept);
	}
}
