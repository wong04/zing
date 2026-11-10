using Zing.Core;
using Zing.Core.LinkPreview;

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
		Assert.Equal(new NavigationDecision.Redirect("https://example.com/"), events.Navigating("about:blank", "https://example.com/?fbclid=abc", NavigationCause.Other));
		Assert.Equal(NavigationDecision.Allow, events.Navigating("about:blank", "https://example.com/", NavigationCause.Other));
	}

	[Fact]
	public void With_blockTrackers_off_navigations_keep_their_parameters()
	{
		var block = false;
		var tabs = new TabService(new TabStore(Deterministic.Clock(), Deterministic.Ids()), _host, "about:blank", () => { }, (_, _) => { }, blockTrackers: () => block);
		var tab = tabs.Open("about:blank");
		tabs.Navigate(tab.Id, "https://example.com/?utm_source=x&id=1");
		Assert.Equal("https://example.com/?utm_source=x&id=1", tabs.Get(tab.Id).Url);
		var events = ((FakePage)tabs.PageOf(tab.Id)!).Events;
		Assert.Equal(NavigationDecision.Allow, events.Navigating("about:blank", "https://example.com/?fbclid=abc", NavigationCause.Other));
		Assert.Equal("https://example.com/?gclid=1", tabs.CleanLink("https://example.com/?gclid=1"));

		// Read at each navigation, so turning it back on applies to open tabs.
		block = true;
		Assert.Equal(new NavigationDecision.Redirect("https://example.com/"), events.Navigating("about:blank", "https://example.com/?fbclid=abc", NavigationCause.Other));
		Assert.Equal("https://example.com/", tabs.CleanLink("https://example.com/?gclid=1"));
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
		var popup = ((FakePage)_tabs.PageOf(opener.Id)!).Events.Popup(new PopupRequest("https://login.example", default, false));
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
		((FakePage)_tabs.PageOf(opener.Id)!).Events.Popup(new PopupRequest("https://b.example", new Modifiers(Shift: false, Ctrl: true, Middle: false), false));
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
	public void Close_others_gives_no_page_to_the_tabs_it_closes()
	{
		var keep = _tabs.Open("https://a.example");
		_tabs.Open("https://b.example");
		_tabs.Create("https://c.example");
		_tabs.Create("https://d.example");
		_tabs.CloseOthers(keep.Id);
		Assert.Equal(2, _host.Created.Count);
	}

	[Fact]
	public void Apply_order_refuses_the_wrong_number_of_ids()
	{
		_tabs.Open("https://a.example");
		Assert.Throws<TabOrderException>(() => _tabs.ApplyOrder([]));
	}

	[Fact]
	public void Apply_order_that_crosses_the_pinned_divider_changes_nothing()
	{
		var pinned = _tabs.Open("https://p.example");
		_tabs.Pin(pinned.Id);
		var a = _tabs.Open("https://a.example");
		var b = _tabs.Open("https://b.example");
		Assert.Throws<TabOrderException>(() => _tabs.ApplyOrder([b.Id, a.Id, pinned.Id]));
		Assert.Equal([pinned.Id, a.Id, b.Id], _tabs.Records.Select(r => r.Id));
	}

	[Fact]
	public void Apply_order_with_a_repeated_or_unknown_id_changes_nothing()
	{
		var a = _tabs.Open("https://a.example");
		var b = _tabs.Open("https://b.example");
		var c = _tabs.Open("https://c.example");
		Assert.Throws<TabOrderException>(() => _tabs.ApplyOrder([c.Id, c.Id, a.Id]));
		Assert.Equal([a.Id, b.Id, c.Id], _tabs.Records.Select(r => r.Id));
		Assert.Throws<TabOrderException>(() => _tabs.ApplyOrder([c.Id, "nope", a.Id]));
		Assert.Equal([a.Id, b.Id, c.Id], _tabs.Records.Select(r => r.Id));
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
		Assert.Equal(NavigationDecision.Allow, events.Navigating("https://a.example/", "https://a.example/", NavigationCause.Other));
		Assert.Null(_tabs.FaultOf(tab.Id));
	}

	[Fact]
	public void A_navigation_rewritten_to_a_clean_link_keeps_the_crash_until_the_clean_one_starts()
	{
		var tab = _tabs.Open("https://a.example/");
		var events = PageOf(tab.Id).Events;
		events.Crashed(PageFault.Crashed);
		events.Navigating("https://a.example/", "https://b.example/?utm_source=x", NavigationCause.Other);
		Assert.Equal(PageFault.Crashed, _tabs.FaultOf(tab.Id));
		events.Navigating("https://a.example/", "https://b.example/", NavigationCause.Other);
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

#region link previews

public sealed class TabPreviewServiceTests
{
	private readonly FakePageHost _host = new();
	private readonly ManualScheduler _scheduler = new();
	private readonly TabService _tabs;
	private PeekSettings _settings = new(Enabled: true, ShiftClick: true, FromNewWindows: true);

	public TabPreviewServiceTests()
	{
		_tabs = new TabService(new TabStore(Deterministic.Clock(), Deterministic.Ids()), _host, "about:blank", () => { }, (_, _) => { }, peekSettings: () => _settings, scheduler: _scheduler);
	}

	private (TabRecord Tab, FakePage Page) OpenSource()
	{
		var tab = _tabs.Open("https://a.example/");
		return (tab, _host.PageFor(_tabs, tab.Id));
	}

	private FakePage Peek(FakePage source)
	{
		Assert.Equal(NavigationDecision.Cancel, source.Events.Navigating("https://a.example/", "https://b.example/", NavigationCause.AnchorClick));
		return (FakePage)_tabs.Preview!.Top.Page;
	}

	private static PopupRequest Window(string url, Modifiers modifiers = default, bool features = false) => new(url, modifiers, features);

	[Fact]
	public void WithBlockTrackersOffAPreviewKeepsTheLinksParameters()
	{
		var tabs = new TabService(new TabStore(Deterministic.Clock(), Deterministic.Ids()), _host, "about:blank", () => { }, (_, _) => { }, peekSettings: () => _settings, scheduler: _scheduler, blockTrackers: () => false);
		var tab = tabs.Open("https://a.example/");
		var page = _host.PageFor(tabs, tab.Id);
		Assert.Equal(NavigationDecision.Cancel, page.Events.Navigating("https://a.example/", "https://b.example/?utm_source=x&id=1", NavigationCause.AnchorClick));
		var shown = (FakePage)tabs.Preview!.Top.Page;
		Assert.Equal(["navigate https://b.example/?utm_source=x&id=1"], shown.Calls);
		Assert.Equal(NavigationDecision.Allow, shown.Events.Navigating("https://b.example/", "https://b.example/next?fbclid=1", NavigationCause.AnchorClick));
	}

	[Fact]
	public void CrossHostAnchorClickOpensAPreviewNotATab()
	{
		var (tab, page) = OpenSource();
		var decision = page.Events.Navigating("https://a.example/", "https://b.example/?utm_source=x&id=1", NavigationCause.AnchorClick);
		Assert.Equal(NavigationDecision.Cancel, decision);
		Assert.Equal(tab.Id, _tabs.Preview!.SourceTabId);
		Assert.Single(_tabs.Records);
		var shown = (FakePage)Assert.Single(_host.Previews[^1]);
		Assert.Equal(["navigate https://b.example/?id=1"], shown.Calls);
		Assert.Equal(1, _host.PreviewFocuses);
	}

	[Fact]
	public void SameHostClickNavigatesInPlace()
	{
		var (_, page) = OpenSource();
		Assert.Equal(NavigationDecision.Allow, page.Events.Navigating("https://a.example/", "https://a.example/next", NavigationCause.AnchorClick));
		Assert.Null(_tabs.Preview);
		Assert.Empty(_host.Previews);
	}

	[Fact]
	public void WithPreviewsOffACrossHostClickNavigatesInPlace()
	{
		_settings = _settings with { Enabled = false };
		var (_, page) = OpenSource();
		Assert.Equal(NavigationDecision.Allow, page.Events.Navigating("https://a.example/", "https://b.example/", NavigationCause.AnchorClick));
		Assert.Null(_tabs.Preview);
	}

	[Fact]
	public void NewWindowToAnotherHostReturnsThePreviewPage()
	{
		var (_, page) = OpenSource();
		var popup = page.Events.Popup(Window("https://b.example/"));
		Assert.Same(_tabs.Preview!.Top.Page, popup);
		// The engine loads the window itself; navigating it again would lose window.opener.
		Assert.Empty(((FakePage)popup).Calls);
		Assert.Single(_tabs.Records);
	}

	[Fact]
	public void CtrlNewWindowStillOpensABackgroundTab()
	{
		var (tab, page) = OpenSource();
		page.Events.Popup(Window("https://b.example/", new Modifiers(Shift: false, Ctrl: true, Middle: false)));
		Assert.Null(_tabs.Preview);
		Assert.Equal(2, _tabs.Records.Count);
		Assert.Equal(tab.Id, _tabs.ActiveId);
	}

	[Fact]
	public void LinksInsideAPreviewNavigateIt()
	{
		var (_, page) = OpenSource();
		var preview = Peek(page);
		Assert.Equal(NavigationDecision.Allow, preview.Events.Navigating("https://b.example/", "https://c.example/", NavigationCause.AnchorClick));
		Assert.Single(_tabs.Preview!.Layers);
	}

	[Fact]
	public void LinksInsideAPreviewAreStillInspected()
	{
		var warning = new NavigationDecision.Redirect("zing://warning?to=x");
		var asked = new List<string>();
		var tabs = new TabService(new TabStore(Deterministic.Clock(), Deterministic.Ids()), _host, "about:blank", () => { }, (_, _) => { }, (id, _, to) =>
		{
			asked.Add($"{id} {to}");
			return to == "https://bad.example/" ? warning : null;
		}, peekSettings: () => _settings);
		var tab = tabs.Open("https://a.example/");
		_host.PageFor(tabs, tab.Id).Events.Navigating("https://a.example/", "https://b.example/", NavigationCause.AnchorClick);
		var preview = (FakePage)tabs.Preview!.Top.Page;
		Assert.Equal(warning, preview.Events.Navigating("https://b.example/", "https://bad.example/", NavigationCause.AnchorClick));
		Assert.Equal($"{tab.Id} https://bad.example/", asked[^1]);
	}

	[Fact]
	public void PopupFromAPreviewStacksALayer()
	{
		var (_, page) = OpenSource();
		var preview = Peek(page);
		var popup = preview.Events.Popup(Window("https://c.example/login", features: true));
		var session = _tabs.Preview!;
		Assert.Equal(2, session.Layers.Count);
		Assert.True(session.Layers[1].IsPopup);
		Assert.Same(popup, session.Top.Page);
		Assert.False(session.CanExpand);
		Assert.Equal([preview, popup], _host.Previews[^1]);
	}

	[Fact]
	public void PopupLayerClosingItselfShowsTheLayerBelow()
	{
		var (_, page) = OpenSource();
		var preview = Peek(page);
		var popup = (FakePage)preview.Events.Popup(Window("https://c.example/login"));
		popup.Events.Closed();
		Assert.True(popup.Closed);
		Assert.Same(preview, Assert.Single(_tabs.Preview!.Layers).Page);
		Assert.Equal([preview], _host.Previews[^1]);
	}

	[Fact]
	public void PreviewPageClosingItselfClosesPreview()
	{
		var (_, page) = OpenSource();
		var preview = Peek(page);
		preview.Events.Closed();
		Assert.Null(_tabs.Preview);
		Assert.Empty(_host.Previews[^1]);
		Assert.Equal("https://b.example/", _tabs.ReopenClosed()!.Url);
	}

	[Fact]
	public void ClosePreviewPushesReopenableTab()
	{
		var (_, page) = OpenSource();
		var preview = Peek(page);
		preview.Title = "B";
		_tabs.ClosePreview();
		Assert.True(preview.Closed);
		Assert.Null(_tabs.Preview);
		var reopened = _tabs.ReopenClosed()!;
		Assert.Equal(("https://b.example/", "B", false), (reopened.Url, reopened.Title, reopened.Pinned));
	}

	[Fact]
	public void CloseTopLayerClosesOnlyThePopup()
	{
		var (_, page) = OpenSource();
		var preview = Peek(page);
		var popup = (FakePage)preview.Events.Popup(Window("https://c.example/login"));
		_tabs.CloseTopLayer();
		Assert.True(popup.Closed);
		Assert.False(preview.Closed);
		_tabs.CloseTopLayer();
		Assert.Null(_tabs.Preview);
	}

	[Fact]
	public void ActivatingAnotherTabClosesThePreview()
	{
		var (_, page) = OpenSource();
		var other = _tabs.Create("https://c.example/");
		var preview = Peek(page);
		_tabs.Activate(other.Id);
		Assert.Null(_tabs.Preview);
		Assert.True(preview.Closed);
	}

	[Fact]
	public void OpeningATabClosesThePreview()
	{
		var (_, page) = OpenSource();
		var preview = Peek(page);
		_tabs.Open("https://c.example/");
		Assert.Null(_tabs.Preview);
		Assert.True(preview.Closed);
	}

	[Fact]
	public void NavigatingTheSourceTabClosesThePreview()
	{
		var (tab, page) = OpenSource();
		var preview = Peek(page);
		_tabs.Navigate(tab.Id, "https://c.example/");
		Assert.Null(_tabs.Preview);
		Assert.True(preview.Closed);
	}

	[Fact]
	public void ClosingTheSourceTabClosesItsPreview()
	{
		var (tab, page) = OpenSource();
		_tabs.Create("https://c.example/");
		var preview = Peek(page);
		_tabs.Close(tab.Id);
		Assert.Null(_tabs.Preview);
		Assert.True(preview.Closed);
	}

	[Fact]
	public void SourceTabCannotSleepWhilePreviewing()
	{
		var (tab, page) = OpenSource();
		Peek(page);
		Assert.Throws<ActiveTabSleepException>(() => _tabs.Sleep(tab.Id));
	}

	[Fact]
	public void ExpandAdoptsTheSamePage()
	{
		var (tab, page) = OpenSource();
		var other = _tabs.Create("https://z.example/");
		var preview = Peek(page);
		preview.Title = "B";
		var expanded = _tabs.ExpandPreview();
		Assert.Equal([tab.Id, expanded.Id, other.Id], _tabs.Records.Select(r => r.Id));
		Assert.Same(preview, _tabs.PageOf(expanded.Id));
		Assert.Equal(["navigate https://b.example/"], preview.Calls);
		Assert.False(preview.Closed);
		Assert.Null(_tabs.Preview);
		Assert.Equal(expanded.Id, _tabs.ActiveId);
		Assert.Same(preview, _host.Shown);
		Assert.Equal(("https://b.example/", "B"), (_tabs.Get(expanded.Id).Url, _tabs.Get(expanded.Id).Title));
		// The page now reports to its tab.
		preview.Events.Navigated("https://b.example/next");
		Assert.Equal("https://b.example/next", _tabs.Get(expanded.Id).Url);
		_tabs.Close(expanded.Id);
		Assert.Equal(tab.Id, _tabs.ActiveId);
	}

	[Fact]
	public void ExpandUnderAPopupThrows()
	{
		var (_, page) = OpenSource();
		var preview = Peek(page);
		preview.Events.Popup(Window("https://c.example/login"));
		Assert.Throws<InvalidOperationException>(() => _tabs.ExpandPreview());
		Assert.Equal(2, _tabs.Preview!.Layers.Count);
	}

	[Fact]
	public void PendingPopupIsClosedWithItsOpenersTab()
	{
		var (tab, page) = OpenSource();
		var held = (FakePage)page.Events.Popup(Window("about:blank"));
		Assert.Null(_tabs.Preview);
		Assert.Single(_tabs.Records);
		Assert.Empty(_host.Previews);
		held.Events.WindowOpened();
		// Not yet a real document, so it is still held.
		Assert.Equal(NavigationDecision.Allow, held.Events.Navigating("", "about:blank", NavigationCause.Other));
		_tabs.Close(tab.Id);
		Assert.True(held.Closed);
		Assert.Equal(0, _scheduler.PendingCount);
	}

	[Fact]
	public void PendingPopupThatNeverNavigatesBecomesATabASecondAfterItOpens()
	{
		var (tab, page) = OpenSource();
		var held = (FakePage)page.Events.Popup(Window("about:blank"));
		// A slow engine: no time counts until it has the window, so a location set right after it opens still decides it.
		_scheduler.Advance(TimeSpan.FromSeconds(5));
		Assert.Single(_tabs.Records);
		held.Events.WindowOpened();
		_scheduler.Advance(TimeSpan.FromMilliseconds(999));
		Assert.Single(_tabs.Records);
		_scheduler.Advance(TimeSpan.FromMilliseconds(1));
		Assert.Equal(2, _tabs.Records.Count);
		var adopted = _tabs.Records[1];
		Assert.Same(held, _tabs.PageOf(adopted.Id));
		Assert.Equal(adopted.Id, _tabs.ActiveId);
		Assert.False(held.Closed);
		Assert.Empty(held.Calls);
		// Now a tab's page: closing it goes back to the opener.
		held.Events.Closed();
		Assert.Equal(tab.Id, _tabs.ActiveId);
	}

	[Fact]
	public void CtrlBlankWindowOpensABackgroundTabAtOnce()
	{
		var (tab, page) = OpenSource();
		var popup = page.Events.Popup(Window("about:blank", new Modifiers(Shift: false, Ctrl: true, Middle: false)));
		Assert.Equal(2, _tabs.Records.Count);
		Assert.Same(popup, _tabs.PageOf(_tabs.Records[1].Id));
		Assert.Equal(tab.Id, _tabs.ActiveId);
		Assert.Equal(0, _scheduler.PendingCount);
	}

	[Fact]
	public void BlankWindowWithPopupFeaturesOpensATabAtOnce()
	{
		var (_, page) = OpenSource();
		var popup = page.Events.Popup(Window("about:blank", features: true));
		Assert.Equal(2, _tabs.Records.Count);
		Assert.Same(popup, _tabs.PageOf(_tabs.ActiveId!));
		Assert.Equal(0, _scheduler.PendingCount);
	}

	[Fact]
	public void NoticesInAPreviewAreReportedThroughTheSourceTabAndLoadFailuresWithThePreviewsAddress()
	{
		var notices = new List<string>();
		var failures = new List<string>();
		var previewFailures = new List<(string, string)>();
		var tabs = new TabService(new TabStore(Deterministic.Clock(), Deterministic.Ids()), _host, "about:blank", () => { }, (tab, d) => failures.Add($"{tab.Id}: {d}"), pageNotice: (tab, text) => notices.Add($"{tab.Id}: {text}"), peekSettings: () => _settings);
		tabs.PreviewLoadFailed += (url, description) => previewFailures.Add((url, description));
		var source = tabs.Open("https://a.example/");
		_host.PageFor(tabs, source.Id).Events.Navigating("https://a.example/", "https://b.example/", NavigationCause.AnchorClick);
		var preview = (FakePage)tabs.Preview!.Top.Page;
		preview.Events.Notice("refused");
		preview.Events.LoadFailed("ERR_NAME_NOT_RESOLVED");
		// A popup layer the engine hasn't committed yet is known by the address it was opened for.
		var popup = (FakePage)preview.Events.Popup(Window("https://c.example/login"));
		popup.Events.LoadFailed("ERR_CONNECTION_RESET");
		Assert.Equal([$"{source.Id}: refused"], notices);
		// Nothing is said of the source tab, whose page is fine.
		Assert.Empty(failures);
		Assert.Equal([("https://b.example/", "ERR_NAME_NOT_RESOLVED"), ("https://c.example/login", "ERR_CONNECTION_RESET")], previewFailures);
	}

	[Fact]
	public void CrashedPopupLayerClosesAndCanBeReopened()
	{
		var (_, page) = OpenSource();
		var preview = Peek(page);
		var popup = (FakePage)preview.Events.Popup(Window("https://c.example/login"));
		popup.Navigate("https://c.example/login");
		popup.Events.Crashed(PageFault.Crashed);
		Assert.True(popup.Closed);
		Assert.Single(_tabs.Preview!.Layers);
		Assert.Equal("https://c.example/login", _tabs.ReopenClosed()!.Url);
	}

	[Fact]
	public void HeldWindowThatBecomesSameHostOpensAsTab()
	{
		var (_, page) = OpenSource();
		var held = (FakePage)page.Events.Popup(Window("about:blank"));
		Assert.Equal(NavigationDecision.Allow, held.Events.Navigating("about:blank", "https://a.example/next", NavigationCause.Other));
		Assert.Equal(2, _tabs.Records.Count);
		var adopted = _tabs.Records[1];
		Assert.Same(held, _tabs.PageOf(adopted.Id));
		Assert.Equal(adopted.Id, _tabs.ActiveId);
		Assert.Null(_tabs.Preview);
		Assert.Empty(held.Calls);
		Assert.Equal(0, _scheduler.PendingCount);
	}

	[Fact]
	public void HeldWindowThatGoesToAnotherHostOpensAsPreview()
	{
		var (_, page) = OpenSource();
		var held = (FakePage)page.Events.Popup(Window("about:blank"));
		Assert.Equal(NavigationDecision.Allow, held.Events.Navigating("about:blank", "https://b.example/", NavigationCause.Other));
		Assert.Same(held, _tabs.Preview!.Top.Page);
		Assert.Single(_tabs.Records);
		Assert.Equal([held], _host.Previews[^1]);
	}

	[Fact]
	public void ANewDocumentInTheSourceTabClosesThePreview()
	{
		var (_, page) = OpenSource();
		var preview = Peek(page);
		// The source page's own script, Back or a reload: none goes through Navigate.
		page.Events.NewDocument();
		Assert.Null(_tabs.Preview);
		Assert.True(preview.Closed);
	}

	[Fact]
	public void ANewDocumentInThePreviewOrABackgroundTabKeepsThePreview()
	{
		var background = _tabs.Open("https://z.example/");
		var (_, page) = OpenSource();
		var preview = Peek(page);
		preview.Events.NewDocument();
		_host.PageFor(_tabs, background.Id).Events.NewDocument();
		Assert.Same(preview, _tabs.Preview!.Top.Page);
	}

	[Theory]
	[InlineData(false, true, false)]
	[InlineData(false, false, true)]
	[InlineData(true, true, false)]
	public void CtrlOrMiddleInAPreviewOpensABackgroundTabAsOnATab(bool shift, bool ctrl, bool middle)
	{
		var (tab, page) = OpenSource();
		var preview = Peek(page);
		var popup = (FakePage)preview.Events.Popup(Window("https://c.example/", new Modifiers(shift, ctrl, middle)));
		Assert.Same(preview, Assert.Single(_tabs.Preview!.Layers).Page);
		Assert.Equal(2, _tabs.Records.Count);
		var opened = _tabs.Records[1];
		Assert.Same(popup, _tabs.PageOf(opened.Id));
		Assert.Equal("https://c.example/", opened.Url);
		Assert.Equal(tab.Id, _tabs.ActiveId);
		// The engine loads the window itself.
		Assert.Empty(popup.Calls);
	}

	[Fact]
	public void HeldWindowThatSettlesAfterTheUserLeftItsOpenerOpensBehind()
	{
		var (_, page) = OpenSource();
		var other = _tabs.Create("https://z.example/");
		var held = (FakePage)page.Events.Popup(Window("about:blank"));
		_tabs.Activate(other.Id);
		Assert.Equal(NavigationDecision.Allow, held.Events.Navigating("about:blank", "https://b.example/", NavigationCause.Other));
		Assert.Equal(3, _tabs.Records.Count);
		Assert.Same(held, _tabs.PageOf(_tabs.Records[1].Id));
		Assert.Equal(other.Id, _tabs.ActiveId);
		Assert.Null(_tabs.Preview);
	}

	[Fact]
	public void HeldWindowThatTimesOutAfterTheUserLeftItsOpenerOpensBehind()
	{
		var (_, page) = OpenSource();
		var other = _tabs.Create("https://z.example/");
		var held = (FakePage)page.Events.Popup(Window("about:blank"));
		held.Events.WindowOpened();
		_tabs.Activate(other.Id);
		_scheduler.Advance(TimeSpan.FromSeconds(1));
		Assert.Equal(3, _tabs.Records.Count);
		Assert.Equal(other.Id, _tabs.ActiveId);
	}

	[Fact]
	public void ExpandKeepsThePreviewsIcon()
	{
		var (_, page) = OpenSource();
		var preview = Peek(page);
		preview.Events.IconChanged("data:image/png;base64,AA==");
		var expanded = _tabs.ExpandPreview();
		Assert.Equal("data:image/png;base64,AA==", _tabs.State("manual", false).Tabs.Single(t => t.Id == expanded.Id).Icon);
	}

	[Fact]
	public void ALayerIsKnownByItsAddressUntilItsPageHasOne()
	{
		var (_, page) = OpenSource();
		var popup = (FakePage)page.Events.Popup(Window("https://b.example/"));
		Assert.Equal("", popup.Url);
		Assert.Equal("https://b.example/", _tabs.Preview!.Top.Address);
		popup.Url = "https://b.example/landed";
		Assert.Equal("https://b.example/landed", _tabs.Preview.Top.Address);
	}
}

#endregion
