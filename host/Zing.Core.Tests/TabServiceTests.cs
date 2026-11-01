using Zing.Core;

namespace Zing.Core.Tests;

public sealed class TabServiceTests
{
	private readonly FakePageHost _host = new();
	private readonly List<string> _failures = [];
	private int _changes;
	private readonly TabService _tabs;

	public TabServiceTests()
	{
		_tabs = new TabService(new TabStore(Deterministic.Clock(), Deterministic.Ids()), _host, "about:blank", () => _changes++, (tab, d) => _failures.Add($"{tab.Url}: {d}"));
	}

	[Fact]
	public void A_created_tab_gets_no_page_until_it_is_activated()
	{
		var record = _tabs.Create("https://example.com");
		Assert.False(_tabs.HasPage(record.Id));
		_tabs.Activate(record.Id);
		Assert.True(_tabs.HasPage(record.Id));
		Assert.Equal(["navigate https://example.com"], _host.Shown!.Calls);
	}

	[Fact]
	public void Activating_another_tab_hides_the_one_in_front()
	{
		var first = _tabs.Open("https://a.example");
		var second = _tabs.Open("https://b.example");
		Assert.Same(_tabs.PageOf(second.Id), _host.Shown);
		_tabs.Activate(first.Id);
		Assert.Same(_tabs.PageOf(first.Id), _host.Shown);
	}

	[Fact]
	public void Closing_the_last_tab_leaves_a_fresh_one()
	{
		var only = _tabs.Open("https://example.com");
		var page = (FakePage)_tabs.PageOf(only.Id)!;
		_tabs.Close(only.Id);
		Assert.True(page.Closed);
		var fresh = Assert.Single(_tabs.Records);
		Assert.Equal("about:blank", fresh.Url);
		Assert.Equal(fresh.Id, _tabs.ActiveId);
	}

	[Fact]
	public void Closing_the_active_tab_activates_its_neighbour_and_shows_its_page()
	{
		var first = _tabs.Open("https://a.example");
		var second = _tabs.Open("https://b.example");
		_tabs.Close(second.Id);
		Assert.Equal(first.Id, _tabs.ActiveId);
		Assert.Same(_tabs.PageOf(first.Id), _host.Shown);
	}

	[Fact]
	public void Navigating_strips_tracking_parameters()
	{
		var tab = _tabs.Open("about:blank");
		_tabs.Navigate(tab.Id, "https://example.com/?utm_source=x&id=1");
		Assert.Equal("https://example.com/?id=1", _tabs.Get(tab.Id).Url);
		Assert.Equal("navigate https://example.com/?id=1", ((FakePage)_tabs.PageOf(tab.Id)!).Calls[^1]);
	}

	[Fact]
	public void A_page_navigating_to_a_tracked_link_is_redirected_to_the_clean_one()
	{
		var tab = _tabs.Open("about:blank");
		var events = ((FakePage)_tabs.PageOf(tab.Id)!).Events;
		Assert.Equal(new NavigationDecision.Redirect("https://example.com/"), events.Navigating("about:blank", "https://example.com/?fbclid=abc"));
		Assert.Equal(NavigationDecision.Allow, events.Navigating("about:blank", "https://example.com/"));
	}

	[Fact]
	public void Page_events_update_the_record_and_a_blank_title_reads_New_Tab()
	{
		var tab = _tabs.Open("about:blank");
		var events = ((FakePage)_tabs.PageOf(tab.Id)!).Events;
		events.Navigated("https://example.com/");
		events.TitleChanged("Example");
		Assert.Equal(("https://example.com/", "Example"), (_tabs.Get(tab.Id).Url, _tabs.Get(tab.Id).Title));
		events.TitleChanged("");
		Assert.Equal(TabStore.NewTabTitle, _tabs.Get(tab.Id).Title);
	}

	[Fact]
	public void A_popup_is_adopted_beside_its_opener_and_closing_it_returns_there()
	{
		var opener = _tabs.Open("https://a.example");
		_tabs.Open("https://b.example");
		_tabs.Activate(opener.Id);
		var popup = ((FakePage)_tabs.PageOf(opener.Id)!).Events.Popup("https://login.example", background: false);
		var records = _tabs.Records.Select(r => r.Url).ToList();
		Assert.Equal(["https://a.example", "https://login.example", "https://b.example"], records);
		Assert.Same(popup, _host.Shown);
		// The engine loads the popup itself; Zing must not navigate it again or window.opener is lost.
		Assert.Empty(((FakePage)popup).Calls);

		((FakePage)popup).Events.Closed();
		Assert.Equal(opener.Id, _tabs.ActiveId);
		Assert.Equal(2, _tabs.Records.Count);
	}

	[Fact]
	public void A_background_popup_does_not_take_the_front()
	{
		var opener = _tabs.Open("https://a.example");
		((FakePage)_tabs.PageOf(opener.Id)!).Events.Popup("https://b.example", background: true);
		Assert.Equal(opener.Id, _tabs.ActiveId);
		Assert.Equal(2, _tabs.Records.Count);
	}

	[Fact]
	public void Restore_gives_only_the_front_tab_a_page()
	{
		_tabs.Restore([
			new SessionTab("https://a.example", "A", true, false),
			new SessionTab("https://b.example", "", false, true),
		]);
		Assert.Equal(["A", TabStore.NewTabTitle], _tabs.Records.Select(r => r.Title));
		Assert.False(_tabs.HasPage(_tabs.Records[0].Id));
		Assert.Equal(_tabs.Records[1].Id, _tabs.ActiveId);
	}

	[Fact]
	public void Close_others_keeps_pinned_tabs_and_the_one_asked_for()
	{
		var pinned = _tabs.Open("https://p.example");
		_tabs.Pin(pinned.Id);
		_tabs.Open("https://a.example");
		var keep = _tabs.Open("https://b.example");
		_tabs.Open("https://c.example");
		_tabs.CloseOthers(keep.Id);
		Assert.Equal([pinned.Id, keep.Id], _tabs.Records.Select(r => r.Id));
		Assert.Equal(keep.Id, _tabs.ActiveId);
	}

	[Fact]
	public void Apply_order_refuses_the_wrong_number_of_ids()
	{
		_tabs.Open("https://a.example");
		Assert.Throws<TabOrderException>(() => _tabs.ApplyOrder([]));
	}

	[Fact]
	public void A_load_failure_is_reported_with_the_tab()
	{
		var tab = _tabs.Open("https://down.example");
		((FakePage)_tabs.PageOf(tab.Id)!).Events.LoadFailed("ERR_CONNECTION_REFUSED");
		Assert.Equal(["https://down.example: ERR_CONNECTION_REFUSED"], _failures);
	}
}

public sealed class TabSleepServiceTests
{
	private readonly FakePageHost _host = new();
	private readonly TabService _tabs;
	private readonly List<string> _woke = [];

	public TabSleepServiceTests()
	{
		_tabs = new TabService(new TabStore(Deterministic.Clock(), Deterministic.Ids()), _host, "about:blank", () => { }, (_, _) => { });
		_tabs.Woke += (id, _) => _woke.Add(id);
	}

	[Fact]
	public void A_sleeping_tab_drops_its_page_keeps_its_record_and_wakes_at_its_url()
	{
		var background = _tabs.Open("https://a.example/");
		_tabs.Open("https://b.example/");
		var page = (FakePage)_tabs.PageOf(background.Id)!;
		_tabs.Sleep(background.Id);
		Assert.True(page.Closed);
		Assert.False(_tabs.HasPage(background.Id));
		Assert.True(_tabs.State("manual", false).Tabs.Single(t => t.Id == background.Id).Sleeping);

		_tabs.Activate(background.Id);
		Assert.Equal([background.Id], _woke);
		Assert.Equal("navigate https://a.example/", ((FakePage)_tabs.PageOf(background.Id)!).Calls[0]);
		Assert.False(_tabs.IsSleeping(background.Id));
	}

	[Fact]
	public void A_tab_keeps_its_icon_while_asleep_and_loses_it_when_closed()
	{
		var background = _tabs.Open("https://a.example/");
		_tabs.Open("https://b.example/");
		((FakePage)_tabs.PageOf(background.Id)!).Events.IconChanged("data:image/png;base64,AA==");
		_tabs.Sleep(background.Id);
		Assert.Equal("data:image/png;base64,AA==", _tabs.State("manual", false).Tabs.Single(t => t.Id == background.Id).Icon);
	}

	[Fact]
	public void The_active_tab_cannot_sleep()
	{
		var tab = _tabs.Open("https://a.example/");
		Assert.Throws<ActiveTabSleepException>(() => _tabs.Sleep(tab.Id));
	}
}

/// <summary>A tab is a record: it outlives its page's renderer, and Reload brings the page back.</summary>
public sealed class TabCrashServiceTests
{
	private readonly FakePageHost _host = new();
	private int _changes;
	private readonly TabService _tabs;

	public TabCrashServiceTests()
	{
		_tabs = new TabService(new TabStore(Deterministic.Clock(), Deterministic.Ids()), _host, "about:blank", () => _changes++, (_, _) => { });
	}

	private TabView ViewOf(string id) => _tabs.State("manual", false).Tabs.Single(t => t.Id == id);

	private FakePage PageOf(string id) => (FakePage)_tabs.PageOf(id)!;

	[Fact]
	public void A_crashed_page_keeps_its_tab_and_page_and_the_strip_says_so()
	{
		var tab = _tabs.Open("https://a.example/");
		var page = PageOf(tab.Id);
		var before = _changes;
		page.Events.Crashed(PageFault.Crashed);
		Assert.Equal(before + 1, _changes);
		Assert.Equal(PageFault.Crashed, _tabs.FaultOf(tab.Id));
		Assert.Equal("crashed", ViewOf(tab.Id).PageState);
		Assert.Same(page, _tabs.PageOf(tab.Id));
		Assert.False(page.Closed);
		Assert.Equal("https://a.example/", _tabs.Get(tab.Id).Url);
	}

	[Fact]
	public void A_working_page_has_no_page_state_on_the_wire()
	{
		var tab = _tabs.Open("https://a.example/");
		Assert.Null(ViewOf(tab.Id).PageState);
		Assert.DoesNotContain("pageState", System.Text.Json.JsonSerializer.Serialize(ViewOf(tab.Id), ZingJson.Options));
	}

	[Fact]
	public void Reload_clears_the_crash_and_reloads_the_page()
	{
		var tab = _tabs.Open("https://a.example/");
		PageOf(tab.Id).Events.Crashed(PageFault.Crashed);
		_tabs.Reload(tab.Id);
		Assert.Null(_tabs.FaultOf(tab.Id));
		Assert.Null(ViewOf(tab.Id).PageState);
		Assert.Equal("reload", PageOf(tab.Id).Calls[^1]);
	}

	[Fact]
	public void A_new_document_starting_in_the_page_clears_the_crash()
	{
		var tab = _tabs.Open("https://a.example/");
		var events = PageOf(tab.Id).Events;
		events.Crashed(PageFault.Crashed);
		Assert.Equal(NavigationDecision.Allow, events.Navigating("https://a.example/", "https://a.example/"));
		Assert.Null(_tabs.FaultOf(tab.Id));
	}

	[Fact]
	public void A_navigation_rewritten_to_a_clean_link_keeps_the_crash_until_the_clean_one_starts()
	{
		var tab = _tabs.Open("https://a.example/");
		var events = PageOf(tab.Id).Events;
		events.Crashed(PageFault.Crashed);
		events.Navigating("https://a.example/", "https://b.example/?utm_source=x");
		Assert.Equal(PageFault.Crashed, _tabs.FaultOf(tab.Id));
		events.Navigating("https://a.example/", "https://b.example/");
		Assert.Null(_tabs.FaultOf(tab.Id));
	}

	[Fact]
	public void An_unresponsive_page_is_marked_once_and_a_crash_outranks_it()
	{
		var tab = _tabs.Open("https://a.example/");
		var events = PageOf(tab.Id).Events;
		events.Crashed(PageFault.Unresponsive);
		var after = _changes;
		events.Crashed(PageFault.Unresponsive);
		Assert.Equal(after, _changes);
		Assert.Equal("unresponsive", ViewOf(tab.Id).PageState);
		events.Crashed(PageFault.Crashed);
		Assert.Equal("crashed", ViewOf(tab.Id).PageState);
		events.Crashed(PageFault.Unresponsive);
		Assert.Equal("crashed", ViewOf(tab.Id).PageState);
	}

	[Fact]
	public void A_crashed_background_tab_that_sleeps_wakes_with_a_working_page()
	{
		var background = _tabs.Open("https://a.example/");
		_tabs.Open("https://b.example/");
		PageOf(background.Id).Events.Crashed(PageFault.Crashed);
		_tabs.Sleep(background.Id);
		Assert.Null(ViewOf(background.Id).PageState);
		_tabs.Activate(background.Id);
		Assert.Null(_tabs.FaultOf(background.Id));
		Assert.Equal(["navigate https://a.example/"], PageOf(background.Id).Calls);
	}

	[Fact]
	public void A_crash_reported_after_the_tab_closed_changes_nothing()
	{
		var first = _tabs.Open("https://a.example/");
		_tabs.Open("https://b.example/");
		var events = PageOf(first.Id).Events;
		_tabs.Close(first.Id);
		var before = _changes;
		events.Crashed(PageFault.Crashed);
		Assert.Equal(before, _changes);
		Assert.Null(_tabs.FaultOf(first.Id));
	}

	[Fact]
	public void Closing_a_crashed_tab_forgets_the_crash()
	{
		var first = _tabs.Open("https://a.example/");
		_tabs.Open("https://b.example/");
		PageOf(first.Id).Events.Crashed(PageFault.Crashed);
		_tabs.Close(first.Id);
		Assert.Null(_tabs.FaultOf(first.Id));
	}
}
