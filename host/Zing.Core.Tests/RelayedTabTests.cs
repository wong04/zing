using Zing.Core.Net;

namespace Zing.Core.Tests;

public sealed class RelayedTabTests
{
	private readonly FakePageHost _host = new();
	private readonly List<string> _notices = [];
	private readonly TabService _tabs;

	public RelayedTabTests()
	{
		_tabs = new TabService(new TabStore(Deterministic.Clock(), Deterministic.Ids()), _host, "about:blank", () => { }, (_, _) => { }, pageNotice: (tab, text) => _notices.Add($"{tab.Id}: {text}"));
	}

	[Fact]
	public void OpenRelayed_starts_a_tab_on_the_relay_address()
	{
		var record = _tabs.OpenRelayed("https://example.com/a?b=1");
		Assert.Equal("zing-relay://example.com/a?b=1", record.Url);
		Assert.Equal(["navigate zing-relay://example.com/a?b=1"], _host.Shown!.Calls);
	}

	[Fact]
	public void OpenRelayed_takes_what_Open_a_Link_takes()
	{
		Assert.Equal("zing-relay://example.com/", _tabs.OpenRelayed("example.com").Url);
	}

	[Fact]
	public void OpenRelayed_refuses_http_because_the_relay_scheme_stands_for_https()
	{
		Assert.Throws<RelayedAddressError>(() => _tabs.OpenRelayed("http://example.com/"));
	}

	[Fact]
	public void OpenRelayed_refuses_what_is_not_an_address()
	{
		Assert.Throws<BadURLError>(() => _tabs.OpenRelayed("not a url"));
		Assert.Throws<CredentialsInURLError>(() => _tabs.OpenRelayed("https://u:p@example.com/"));
	}

	[Fact]
	public void A_relayed_tab_that_sleeps_wakes_by_a_navigation_Zing_starts()
	{
		var relayed = _tabs.OpenRelayed("https://example.com/");
		_tabs.Open("https://other.example/");
		_tabs.Sleep(relayed.Id);
		Assert.False(_tabs.HasPage(relayed.Id));
		_tabs.Activate(relayed.Id);
		Assert.Equal(["navigate zing-relay://example.com/"], ((FakePage)_tabs.PageOf(relayed.Id)!).Calls);
	}

	[Fact]
	public void A_saved_session_keeps_the_real_address_not_the_relay_one()
	{
		var relayed = _tabs.OpenRelayed("https://example.com/a");
		var saved = Session.ToSessionTabs(_tabs.Records, relayed.Id);
		Assert.Equal("https://example.com/a", saved.Single().Url);
		Assert.Equal(["https://example.com/a"], Session.Prepare(saved).Select(t => t.Url));
	}

	[Fact]
	public void A_page_notice_reaches_the_window()
	{
		var record = _tabs.OpenRelayed("https://example.com/");
		_host.Shown!.Events.Notice("this one is an app");
		Assert.Equal([$"{record.Id}: this one is an app"], _notices);
	}

	[Fact]
	public void Relay_origins_count_as_sites_a_tab_touched_so_their_storage_is_purged_with_it()
	{
		var usage = new SiteUsage();
		usage.Visit("t1", "zing-relay://example.com/a");
		var gone = usage.Close("t1").Single();
		Assert.Equal("example.com", gone.Domain);
		Assert.Equal(["zing-relay://example.com"], gone.Origins);
	}
}
