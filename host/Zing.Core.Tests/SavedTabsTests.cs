using Zing.Core;

namespace Zing.Core.Tests;

/// <summary>Favourites and pinned items as saved tabs (docs/phase-4.md, part B).</summary>
public sealed class SavedTabsTests
{
	private readonly FakePageHost _host = new();
	private readonly TabService _tabs;
	private readonly LibraryStore _library;
	private readonly SavedTabs _saved;
	private int _ids;

	public SavedTabsTests()
	{
		_tabs = new TabService(new TabStore(Deterministic.Clock(), Deterministic.Ids()), _host, "about:blank", () => { }, (_, _) => { });
		_library = new LibraryStore(Library.EmptyLibrary(1), _ => { }, () => 1, () => $"item-{++_ids}");
		_saved = new SavedTabs(_tabs, _library);
	}

	private TabRecord Visit(string url)
	{
		var record = _tabs.Open(url);
		_tabs.Navigate(record.Id, url);
		return _tabs.Get(record.Id);
	}

	[Fact]
	public void Pinning_a_tab_saves_it_to_Pinned_and_ties_the_tab_to_the_item()
	{
		var tab = Visit("https://mail.test/");
		_saved.Pin(tab.Id);
		var item = Assert.IsType<BookmarkLink>(Assert.Single(Library.Pinned(_library.Snapshot)));
		Assert.Equal("https://mail.test/", item.Url);
		var record = _tabs.Get(tab.Id);
		Assert.Equal(item.Id, record.ItemId);
		Assert.True(record.Pinned);
	}

	[Fact]
	public void Opening_an_item_twice_switches_to_its_tab_rather_than_opening_another()
	{
		var tab = Visit("https://mail.test/");
		_saved.Pin(tab.Id);
		var itemId = _tabs.Get(tab.Id).ItemId!;
		Visit("https://other.test/");
		_saved.Open(itemId);
		Assert.Equal(tab.Id, _tabs.ActiveId);
		Assert.Equal(2, _tabs.Records.Count);
	}

	[Fact]
	public void Closing_an_items_tab_keeps_the_item_and_opening_it_again_starts_a_fresh_tab_at_its_address()
	{
		var tab = Visit("https://mail.test/");
		_saved.Pin(tab.Id);
		var itemId = _tabs.Get(tab.Id).ItemId!;
		_tabs.Navigate(tab.Id, "https://mail.test/inbox/42");
		_tabs.Close(tab.Id);
		Assert.Single(Library.Pinned(_library.Snapshot));
		// The item is the way back, so the closed tab isn't offered again as well.
		Assert.False(_tabs.CanReopen);
		_saved.Open(itemId);
		var reopened = _tabs.Get(_tabs.ActiveId!);
		Assert.NotEqual(tab.Id, reopened.Id);
		Assert.Equal(itemId, reopened.ItemId);
		Assert.Equal("https://mail.test/", reopened.Url);
	}

	[Fact]
	public void Reset_goes_back_to_the_saved_address_and_update_saves_the_current_one()
	{
		var tab = Visit("https://mail.test/");
		_saved.Pin(tab.Id);
		_tabs.Navigate(tab.Id, "https://mail.test/inbox/42");
		_saved.Reset(tab.Id);
		Assert.Equal("https://mail.test/", _tabs.Get(tab.Id).Url);
		_tabs.Navigate(tab.Id, "https://mail.test/drafts");
		_saved.Update(tab.Id);
		Assert.Equal("https://mail.test/drafts", Assert.IsType<BookmarkLink>(Assert.Single(Library.Pinned(_library.Snapshot))).Url);
	}

	[Fact]
	public void Unpinning_removes_the_item_and_leaves_the_tab_open_as_an_ordinary_tab()
	{
		var tab = Visit("https://mail.test/");
		_saved.Pin(tab.Id);
		_saved.Unpin(tab.Id);
		Assert.Empty(Library.Pinned(_library.Snapshot));
		var record = _tabs.Get(tab.Id);
		Assert.Null(record.ItemId);
		Assert.False(record.Pinned);
	}

	[Fact]
	public void Removing_an_item_from_the_library_unties_its_tab()
	{
		var tab = Visit("https://mail.test/");
		_saved.Pin(tab.Id);
		_library.Apply(new RemoveAction([_tabs.Get(tab.Id).ItemId!]));
		Assert.Null(_tabs.Get(tab.Id).ItemId);
		Assert.False(_tabs.Get(tab.Id).Pinned);
	}

	[Fact]
	public void Saving_a_tab_into_a_folder_puts_it_at_the_place_given_and_saving_it_again_moves_its_item()
	{
		var first = Visit("https://a.test/");
		_saved.Pin(first.Id);
		var school = _library.Apply(new NewFolderAction([]))!;
		_library.Apply(new MoveIntoAction(school, Library.PinnedId, 0));
		var second = Visit("https://b.test/");
		_saved.Save(second.Id, school, 0);
		Assert.Equal(["https://b.test/"], Assert.IsType<BookmarkFolder>(Library.Find(school, _library.Snapshot.Bookmarks)).Children.Cast<BookmarkLink>().Select(l => l.Url));
		var itemId = _tabs.Get(second.Id).ItemId!;
		_saved.Save(second.Id, Library.FavouritesId, 0);
		Assert.Equal(itemId, Assert.Single(Library.Favourites(_library.Snapshot)).Id);
		Assert.Empty(Assert.IsType<BookmarkFolder>(Library.Find(school, _library.Snapshot.Bookmarks)).Children);
	}

	[Fact]
	public void Favouriting_ties_the_tab_and_an_address_already_in_Favourites_ties_to_that_item()
	{
		var tab = Visit("https://mail.test/");
		Assert.Equal(AddOutcome.Added, _saved.Favourite(tab.Id));
		var itemId = _tabs.Get(tab.Id).ItemId;
		Assert.Equal(Assert.Single(Library.Favourites(_library.Snapshot)).Id, itemId);
		var again = Visit("https://mail.test/");
		Assert.Equal(AddOutcome.Exists, _saved.Favourite(again.Id));
		// The item already has its tab; this one stays an ordinary tab.
		Assert.Null(_tabs.Get(again.Id).ItemId);
	}

	[Fact]
	public void Pinned_tabs_from_an_older_session_become_pinned_items()
	{
		_tabs.Restore([new SessionTab("https://old.test/", "Old", Pinned: true, Active: true), new SessionTab("https://loose.test/", "Loose", Pinned: false, Active: false)]);
		_saved.AdoptPinnedTabs();
		var item = Assert.IsType<BookmarkLink>(Assert.Single(Library.Pinned(_library.Snapshot)));
		Assert.Equal(("Old", "https://old.test/"), (item.Title, item.Url));
		Assert.Equal(item.Id, _tabs.Records.Single(t => t.Url == "https://old.test/").ItemId);
	}

	[Fact]
	public void A_restored_tab_keeps_its_item()
	{
		var tab = Visit("https://mail.test/");
		_saved.Pin(tab.Id);
		var session = Session.ToSessionTabs(_tabs.Records, _tabs.ActiveId);
		Assert.Equal(_tabs.Get(tab.Id).ItemId, Assert.Single(session).ItemId);
	}
}
