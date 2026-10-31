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
		Assert.Equal("https://example.com/", events.Redirect("https://example.com/?fbclid=abc"));
		Assert.Null(events.Redirect("https://example.com/"));
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
