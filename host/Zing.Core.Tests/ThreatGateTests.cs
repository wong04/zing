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

	private NavigationDecision? Check(string from, string to, string tab = "tab-1") => _gate.Check(tab, from, to);

	private static string WarningUrlOf(NavigationDecision? decision) => Assert.IsType<NavigationDecision.Redirect>(decision).Url;

	private static string Html(StartPageResponse? response) => Assert.IsType<StartPageResponse>(response).Body;

	private StartPageResponse? Serve(string url) => _gate.Serve(url, new SettingsStore().Snapshot());

	[GeneratedRegex("href=\"(zing://visit-anyway[^\"]*)\"")]
	private static partial Regex VisitLink();

	/// <summary>The warning page for <paramref name="to"/> and the link a person would click on it.</summary>
	private (string Warning, string Visit) Warn(string to, string from = Page, string tab = "tab-1")
	{
		var warning = WarningUrlOf(Check(from, to, tab));
		var match = VisitLink().Match(Html(Serve(warning)));
		Assert.True(match.Success, "the warning page has a Visit anyway link");
		return (warning, WebUtility.HtmlDecode(match.Groups[1].Value));
	}

	// MARK: - Deciding

	[Fact]
	public void A_clean_address_goes_through() => Assert.Null(Check(Page, Clean));

	[Fact]
	public void A_flagged_address_is_sent_to_the_warning_which_carries_it_encoded()
	{
		var warning = WarningUrlOf(Check(Page, Flagged));
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
	public void Only_web_addresses_are_inspected(string to) => Assert.Null(Check(Page, to));

	[Fact]
	public void Turning_the_setting_off_takes_effect_on_the_next_navigation()
	{
		Assert.NotNull(Check(Page, Flagged));
		_on = false;
		Assert.Null(Check(Page, Flagged));
		_on = true;
		Assert.NotNull(Check(Page, Flagged));
	}

	[Fact]
	public void A_redirect_to_a_flagged_address_is_inspected_like_any_navigation()
	{
		// The engine reports a redirect as a navigation from the page that is still showing.
		Assert.NotNull(Check("https://short.example/abc", Flagged));
	}

	[Fact]
	public void A_notice_alone_does_not_warn() => Assert.Null(Check(Page, "https://185.243.115.24/"));

	[Fact]
	public void A_local_address_stands_in_for_a_flagged_one_only_when_the_gate_was_given_the_mapping()
	{
		var local = "http://127.0.0.1:5000/phish";
		Assert.Null(Check(Page, local));
		var mapped = new ThreatGate(() => true, uri => uri.Port == 5000 ? new UriBuilder(uri) { Host = "apple.com.secure-login.tk" }.Uri : uri);
		Assert.NotNull(mapped.Check("tab-1", Page, local));
	}

	// MARK: - Relayed pages

	[Fact]
	public void A_relayed_load_is_inspected_on_the_address_it_stands_for()
	{
		Assert.Null(Check(Page, "zing-relay://github.com/"));
		var warning = WarningUrlOf(Check("zing://home", "zing-relay://apple.com.secure-login.tk/verify"));
		Assert.Equal(Flagged, StartPage.QueryParam(warning, "to"));
		Assert.Contains("Open a Link", Html(Serve(warning)));
	}

	[Fact]
	public void A_relayed_warning_does_not_say_the_site_was_not_contacted_and_a_plain_one_does()
	{
		var relayed = Html(Serve(WarningUrlOf(Check("zing://home", "zing-relay://apple.com.secure-login.tk/verify"))));
		Assert.DoesNotContain("has not been contacted", relayed);
		Assert.Contains("may already have been asked", relayed);
		var plain = Html(Serve(WarningUrlOf(Check(Page, Flagged))));
		Assert.Contains("has not been contacted", plain);
	}

	[Fact]
	public void Visiting_a_flagged_relayed_address_anyway_goes_back_through_the_relay()
	{
		var (warning, visit) = Warn("zing-relay://apple.com.secure-login.tk/verify", "zing://home");
		var decision = Check(warning, visit);
		Assert.Equal("zing-relay://apple.com.secure-login.tk/verify", Assert.IsType<NavigationDecision.Redirect>(decision).Url);
	}

	// MARK: - Visit anyway

	[Fact]
	public void Visit_anyway_from_the_warning_loads_the_address_and_does_not_warn_again_for_the_host()
	{
		var (warning, visit) = Warn(Flagged);
		Assert.Equal(Flagged, Assert.IsType<NavigationDecision.Redirect>(Check(warning, visit)).Url);
		Assert.Null(Check(Page, Flagged));
		Assert.Null(Check(Page, "https://apple.com.secure-login.tk/other/page"));
		Assert.NotNull(Check(Page, "https://paypal.account-update.xyz/signin"));
	}

	[Fact]
	public void The_allowed_hosts_are_not_shared_with_another_session() =>
		Assert.NotNull(new ThreatGate(() => true).Check("tab-1", Page, Flagged));

	[Theory]
	[InlineData("https://evil.example/page")]
	[InlineData("zing://home")]
	[InlineData("")]
	[InlineData("about:blank")]
	public void Visit_anyway_from_anywhere_but_the_warning_is_refused_and_allows_nothing(string from)
	{
		var (_, visit) = Warn(Flagged);
		Assert.IsType<NavigationDecision.Cancelled>(Check(from, visit));
		Assert.NotNull(Check(Page, Flagged));
	}

	[Fact]
	public void Visit_anyway_from_a_warning_for_another_address_is_refused()
	{
		var (_, visit) = Warn(Flagged);
		var otherWarning = WarningUrlOf(Check(Page, "https://paypal.account-update.xyz/signin"));
		Assert.IsType<NavigationDecision.Cancelled>(Check(otherWarning, visit));
		Assert.NotNull(Check(Page, Flagged));
	}

	[Fact]
	public void A_page_that_reaches_the_warning_window_cannot_use_it_without_the_key_the_warning_page_carries()
	{
		// A script that opened the window the warning is in can set its location; the key is on the page it cannot read.
		var (warning, _) = Warn(Flagged);
		var forged = "zing://visit-anyway?to=" + Uri.EscapeDataString(Flagged);
		Assert.IsType<NavigationDecision.Cancelled>(Check(warning, forged));
		Assert.IsType<NavigationDecision.Cancelled>(Check(warning, forged + "&k=0000"));
		Assert.NotNull(Check(Page, Flagged));
	}

	[Fact]
	public void A_key_works_for_the_address_it_was_issued_for_only()
	{
		var (warning, visit) = Warn(Flagged);
		var other = "https://paypal.account-update.xyz/signin";
		var swapped = Regex.Replace(visit, "to=[^&]*", "to=" + Uri.EscapeDataString(other));
		Assert.IsType<NavigationDecision.Cancelled>(Check(warning, swapped));
		Assert.IsType<NavigationDecision.Cancelled>(Check(WarningUrlOf(Check(Page, other)), swapped));
		Assert.NotNull(Check(Page, other));
	}

	[Fact]
	public void A_key_is_used_once()
	{
		var (warning, visit) = Warn(Flagged);
		Assert.IsType<NavigationDecision.Redirect>(Check(warning, visit));
		Assert.IsType<NavigationDecision.Cancelled>(Check(warning, visit));
	}

	[Fact]
	public void Reloading_the_warning_gives_a_working_link()
	{
		var (warning, _) = Warn(Flagged);
		var again = VisitLink().Match(Html(Serve(warning))).Groups[1].Value;
		Assert.IsType<NavigationDecision.Redirect>(Check(warning, WebUtility.HtmlDecode(again)));
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
		Assert.IsType<NavigationDecision.Cancelled>(Check(warning, visit));
	}

	[Fact]
	public void Visit_anyway_with_no_address_is_refused() =>
		Assert.IsType<NavigationDecision.Cancelled>(Check("zing://warning?to=x", "zing://visit-anyway"));

	[Fact]
	public void Visit_anyway_works_even_if_the_setting_has_since_been_turned_off()
	{
		var (warning, visit) = Warn(Flagged);
		_on = false;
		Assert.IsType<NavigationDecision.Redirect>(Check(warning, visit));
	}

	// MARK: - The warning page

	[Fact]
	public void The_page_names_the_host_plainly_with_the_reasons_and_says_it_is_a_guess_from_the_address()
	{
		var body = Html(Serve(WarningUrlOf(Check(Page, Flagged))));
		Assert.Contains("apple.com.secure-login.tk", body);
		Assert.Contains("Impersonates Apple", body);
		Assert.Contains("High-risk domain ending (.tk)", body);
		Assert.Contains("guess", body);
		Assert.Contains("nothing was sent", body, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public void Go_back_is_the_main_action_and_visit_anyway_the_smaller_one()
	{
		var body = Html(Serve(WarningUrlOf(Check(Page, Flagged))));
		Assert.True(body.IndexOf("Go back", StringComparison.Ordinal) is var back and > 0 && body.IndexOf("Visit anyway", StringComparison.Ordinal) > back);
		Assert.Contains("class=\"primary\"", body);
		Assert.Contains("class=\"minor\"", body);
	}

	[Fact]
	public void Go_back_does_not_loop_and_has_somewhere_to_go_with_no_history()
	{
		var body = Html(Serve(WarningUrlOf(Check(Page, Flagged))));
		Assert.Contains("history.length > 1", body);
		Assert.Contains("history.back()", body);
		Assert.Contains("location.replace('zing://home')", body);
	}

	[Theory]
	[InlineData("")]
	[InlineData("about:blank")]
	public void A_warning_with_no_page_behind_it_goes_to_the_start_page_and_never_back(string from)
	{
		var body = Html(Serve(WarningUrlOf(Check(from, Flagged))));
		Assert.Contains("location.replace('zing://home')", body);
		Assert.DoesNotContain("history.back()", body);
	}

	[Fact]
	public void The_address_is_text_on_the_page_and_never_markup()
	{
		var evil = "https://apple.com.secure-login.tk/<script>alert(1)</script>\"onmouseover=\"x'&to=https://nested.example/";
		var warning = WarningUrlOf(Check(Page, evil));
		var body = Html(Serve(warning));
		Assert.DoesNotContain("<script>alert", body);
		Assert.DoesNotContain("\"onmouseover=\"", body);
		Assert.Contains("&amp;to=https://nested.example/", body);
		// The nested to= stays inside the one encoded value, and comes back whole.
		var visit = WebUtility.HtmlDecode(VisitLink().Match(body).Groups[1].Value);
		Assert.Equal(new Uri(evil).AbsoluteUri, StartPage.QueryParam(visit, "to"));
		Assert.Equal(new Uri(evil).AbsoluteUri, StartPage.QueryParam(warning, "to"));
		Assert.IsType<NavigationDecision.Redirect>(Check(warning, visit));
		Assert.Null(Check(Page, "https://nested.example/"));
	}

	[Fact]
	public void A_crafted_address_never_reaches_the_page_as_markup()
	{
		var crafted = "https://apple.com.secure-login.tk/<script>alert(1)</script>\"><img src=x onerror=alert(2)>";
		var body = Html(Serve(WarningUrlOf(Check(Page, crafted))));
		Assert.DoesNotContain("<script>alert", body);
		Assert.DoesNotContain("<img", body);
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
		var response = Assert.IsType<StartPageResponse>(Serve(WarningUrlOf(Check(Page, Flagged))));
		Assert.Equal("no-store", response.CacheControl);
		Assert.Equal(200, response.Status);
	}

	[Fact]
	public void Other_zing_addresses_are_not_the_gates_to_serve()
	{
		Assert.Null(Serve("zing://home"));
		Assert.Null(Serve("zing://visit-anyway?to=" + Uri.EscapeDataString(Flagged)));
	}


	// MARK: - What Visit anyway allows

	[Fact]
	public void Visit_anyway_allows_that_scheme_host_and_port_only()
	{
		var (warning, visit) = Warn(Flagged);
		Check(warning, visit);
		Assert.Null(Check(Page, Flagged));
		Assert.NotNull(Check(Page, "http://apple.com.secure-login.tk/verify"));
		Assert.NotNull(Check(Page, "https://apple.com.secure-login.tk:8443/verify"));
	}

	[Fact]
	public void An_allowed_host_still_warns_about_a_link_that_hides_its_destination_behind_another_name()
	{
		var (warning, visit) = Warn(Flagged);
		Check(warning, visit);
		Assert.NotNull(Check(Page, "https://www.google.com@apple.com.secure-login.tk/"));
	}

	[Fact]
	public void Visiting_a_disguised_link_anyway_allows_that_exact_link_and_not_the_host()
	{
		var disguised = "https://www.google.com@apple.com.secure-login.tk/x";
		var (warning, visit) = Warn(disguised);
		Check(warning, visit);
		Assert.Null(Check(Page, disguised));
		Assert.NotNull(Check(Page, "https://www.google.com@apple.com.secure-login.tk/y"));
		Assert.NotNull(Check(Page, "https://apple.com.secure-login.tk/x"));
	}

	// MARK: - What a page can say about a warning

	[Fact]
	public void A_page_cannot_make_the_warning_claim_a_relay_or_a_blank_history()
	{
		var crafted = "zing://warning?to=" + Uri.EscapeDataString(Flagged) + "&via=relay&back=home";
		var rebuilt = WarningUrlOf(Check(Page, crafted));
		Assert.DoesNotContain("via=", rebuilt);
		Assert.DoesNotContain("back=", rebuilt);
		var body = Html(Serve(rebuilt));
		Assert.DoesNotContain("Open a Link", body);
		Assert.Contains("history.back()", body);
		var visit = WebUtility.HtmlDecode(VisitLink().Match(body).Groups[1].Value);
		Assert.Equal(Flagged, Assert.IsType<NavigationDecision.Redirect>(Check(rebuilt, visit)).Url);
	}

	[Fact]
	public void A_warning_page_asked_for_without_the_host_having_made_it_says_nothing()
	{
		var response = Assert.IsType<StartPageResponse>(Serve("zing://warning?to=" + Uri.EscapeDataString(Flagged) + "&via=relay&back=home"));
		Assert.Equal(400, response.Status);
		Assert.DoesNotContain("visit-anyway", response.Body);
		Assert.DoesNotContain("Open a Link", response.Body);
	}

	[Fact]
	public void A_warning_that_is_already_live_in_its_tab_is_left_alone()
	{
		var (warning, _) = Warn(Flagged);
		Assert.Null(Check(Page, warning));
	}

	[Fact]
	public void A_warning_for_an_address_the_check_finds_fine_is_left_for_the_page_to_refuse() =>
		Assert.Null(Check(Page, "zing://warning?to=" + Uri.EscapeDataString(Clean)));

	// MARK: - One warning per tab

	[Fact]
	public void A_page_in_another_tab_cannot_break_a_warning_waiting_for_a_click()
	{
		var (warning, visit) = Warn(Flagged, tab: "tab-1");
		for (var i = 0; i < 300; i++)
		{
			Check(Page, "zing://warning?to=" + Uri.EscapeDataString($"https://apple.com.secure-login.tk/{i}"), "evil");
			Check(Page, $"https://paypal.account-update.xyz/{i}", "evil");
		}
		Assert.Equal(2, _gate.KeysHeld);
		Assert.IsType<NavigationDecision.Redirect>(Check(warning, visit, "tab-1"));
	}

	[Fact]
	public void A_tabs_next_warning_replaces_its_last_and_only_that_tab_is_affected()
	{
		var (firstWarning, firstVisit) = Warn(Flagged, tab: "tab-1");
		var other = Warn("https://paypal.account-update.xyz/signin", tab: "tab-2");
		var (secondWarning, secondVisit) = Warn("https://signin-netflix.billing-verify.top/", tab: "tab-1");
		Assert.IsType<NavigationDecision.Cancelled>(Check(firstWarning, firstVisit, "tab-1"));
		Assert.IsType<NavigationDecision.Redirect>(Check(secondWarning, secondVisit, "tab-1"));
		Assert.IsType<NavigationDecision.Redirect>(Check(other.Warning, other.Visit, "tab-2"));
	}

	[Fact]
	public void A_key_taken_to_another_tab_does_nothing()
	{
		var (warning, visit) = Warn(Flagged, tab: "tab-1");
		Assert.IsType<NavigationDecision.Cancelled>(Check(warning, visit, "tab-2"));
		Assert.IsType<NavigationDecision.Redirect>(Check(warning, visit, "tab-1"));
	}

	// MARK: - A warning that outlived the process (session restore, pinned tab, Back)

	[Fact]
	public void A_warning_address_with_a_dead_key_becomes_a_fresh_working_warning()
	{
		var (old, _) = Warn(Flagged);
		var restarted = new ThreatGate(() => true);
		var rebuilt = Assert.IsType<NavigationDecision.Redirect>(restarted.Check("tab-9", "", old)).Url;
		Assert.NotEqual(old, rebuilt);
		var body = Assert.IsType<StartPageResponse>(restarted.Serve(rebuilt, new SettingsStore().Snapshot())).Body;
		Assert.Contains("location.replace('zing://home')", body);
		Assert.DoesNotContain("history.back()", body);
		var visit = WebUtility.HtmlDecode(VisitLink().Match(body).Groups[1].Value);
		Assert.Equal(Flagged, Assert.IsType<NavigationDecision.Redirect>(restarted.Check("tab-9", rebuilt, visit)).Url);
	}

	[Fact]
	public void Back_to_a_warning_after_visiting_anyway_shows_the_warning_and_does_not_bounce_on()
	{
		var (warning, visit) = Warn(Flagged);
		Check(warning, visit);
		var again = WarningUrlOf(Check(Flagged, warning));
		Assert.StartsWith("zing://warning?to=", again);
		Assert.Equal(200, Assert.IsType<StartPageResponse>(Serve(again)).Status);
	}

	[Fact]
	public void A_withheld_document_is_the_one_that_was_sent_to_a_warning_and_only_as_a_document()
	{
		Assert.True(ThreatGate.IsWithheld(Flagged, Flagged + "#top", true));
		Assert.False(ThreatGate.IsWithheld(Flagged, Flagged, false));
		Assert.False(ThreatGate.IsWithheld(Flagged, Clean, true));
		Assert.False(ThreatGate.IsWithheld(null, Flagged, true));
		var relay = "zing-relay://apple.com.secure-login.tk/verify";
		Assert.True(ThreatGate.IsWithheld(relay, relay, true));
	}

	[Fact]
	public void The_address_a_warning_stands_for_is_what_a_session_or_pinned_tab_keeps()
	{
		var (warning, _) = Warn(Flagged);
		Assert.Equal(Flagged, Zing.Core.Net.RelayedContent.ToWebAddress(warning));
		Assert.Equal("zing://warning?to=x", Zing.Core.Net.RelayedContent.ToWebAddress("zing://warning?to=x"));
		Assert.Equal("zing://home", Zing.Core.Net.RelayedContent.ToWebAddress("zing://home"));
	}
}
