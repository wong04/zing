using Zing.Core;

namespace Zing.Core.Tests;

public sealed class TabStoreTests
{
	private long _time = 1000;

	private TabStore NewStore() => new(() => _time);

	[Fact]
	public void Opening_a_tab_makes_it_active_with_the_new_tab_title()
	{
		var store = NewStore();
		var tab = store.Open("zing://home/");
		Assert.Equal(tab.Id, store.ActiveId);
		Assert.Equal(TabStore.NewTabTitle, tab.Title);
		Assert.Equal(1000, tab.CreatedAt);
	}

	[Fact]
	public void Updates_change_only_what_is_given_and_stamp_updatedAt()
	{
		var store = NewStore();
		var tab = store.Open("about:blank");
		_time = 2000;
		store.Update(tab.Id, url: "https://example.com/");
		var updated = store.Get(tab.Id);
		Assert.Equal("https://example.com/", updated.Url);
		Assert.Equal(TabStore.NewTabTitle, updated.Title);
		Assert.Equal(2000, updated.UpdatedAt);
	}

	[Fact]
	public void A_blank_title_falls_back_to_new_tab()
	{
		var store = NewStore();
		var tab = store.Open("about:blank");
		store.Update(tab.Id, title: "  ");
		Assert.Equal(TabStore.NewTabTitle, store.Get(tab.Id).Title);
	}

	[Fact]
	public void The_snapshot_carries_page_state_and_the_strip_settings()
	{
		var store = NewStore();
		var tab = store.Open("https://example.com/");
		store.SetPage(tab.Id, new PageState(Loading: true, CanGoBack: true, CanGoForward: false));
		var state = store.Snapshot("by-site", dropdowns: true);
		var view = Assert.Single(state.Tabs);
		Assert.True(view.Loading);
		Assert.True(view.CanGoBack);
		Assert.Equal(tab.Id, state.ActiveId);
		Assert.Equal("by-site", state.Organization);
		Assert.True(state.Dropdowns);
	}

	[Fact]
	public void Unknown_tabs_are_errors_not_no_ops()
	{
		var store = NewStore();
		Assert.Throws<UnknownTabException>(() => store.Activate("nope"));
		Assert.Throws<UnknownTabException>(() => store.Update("nope", title: "x"));
		Assert.Throws<UnknownTabException>(() => store.SetPage("nope", new PageState(false, false, false)));
	}
}
