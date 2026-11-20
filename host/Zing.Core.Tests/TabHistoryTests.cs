using System.Text.Json;
using Zing.Core;

namespace Zing.Core.Tests;

/// <summary>A tab's back/forward history across sleeping and a restored session (CarriedHistory).</summary>
public sealed class TabHistoryTests
{
	private readonly FakePageHost _host = new();
	private readonly TabService _tabs;

	public TabHistoryTests()
	{
		_tabs = new TabService(new TabStore(Deterministic.Clock(), Deterministic.Ids()), _host, "about:blank", () => { }, (_, _) => { });
	}

	private static PageHistory Engine(int index, params string[] names) =>
		new([.. names.Select((n, i) => new HistoryEntry(i + 1, $"https://x.test/{n}", n.ToUpperInvariant()))], index);

	private static TabHistory Saved(int index, params string[] names) =>
		new([.. names.Select(n => new TabHistoryEntry($"https://x.test/{n}", n.ToUpperInvariant()))], index);

	private FakePage PageOf(string id) => (FakePage)_tabs.PageOf(id)!;

	/// <summary>A tab on c that went a, b, c, d and came back to c, then slept behind another tab and was shown again.</summary>
	private string SleptAndWoken()
	{
		var tab = _tabs.Open("https://x.test/c");
		var page = PageOf(tab.Id);
		page.History = Engine(2, "a", "b", "c", "d");
		page.CanGoBack = page.CanGoForward = true;
		_tabs.Open("https://y.test/");
		_tabs.Sleep(tab.Id);
		_tabs.Activate(tab.Id);
		Assert.NotSame(page, PageOf(tab.Id));
		return tab.Id;
	}

	[Fact]
	public void A_woken_tab_goes_back_and_forward_through_the_history_it_had_before_it_slept()
	{
		var id = SleptAndWoken();
		var page = PageOf(id);
		Assert.Equal("navigate https://x.test/c", page.Calls[0]);
		Assert.True(_tabs.CanGoBack(id));
		Assert.True(_tabs.CanGoForward(id));

		_tabs.GoBack(id);
		_tabs.GoBack(id);
		Assert.Equal(["replace https://x.test/b", "replace https://x.test/a"], page.Calls.Skip(1));
		Assert.Equal("https://x.test/a", _tabs.Get(id).Url);
		Assert.False(_tabs.CanGoBack(id));

		_tabs.GoForward(id);
		_tabs.GoForward(id);
		_tabs.GoForward(id);
		Assert.Equal(["replace https://x.test/b", "replace https://x.test/c", "replace https://x.test/d"], page.Calls.Skip(3));
		Assert.False(_tabs.CanGoForward(id));
		Assert.DoesNotContain("back", page.Calls);
		Assert.DoesNotContain("forward", page.Calls);
	}

	[Fact]
	public void A_woken_tab_leaves_Zings_own_pages_out_of_its_history()
	{
		var tab = _tabs.Open("https://x.test/a");
		var page = PageOf(tab.Id);
		page.History = new([new(1, "zing-start://new-tab/", "New tab"), new(2, "https://x.test/a", "A")], 1);
		page.CanGoBack = true;
		_tabs.Open("https://y.test/");
		_tabs.Sleep(tab.Id);
		_tabs.Activate(tab.Id);
		Assert.False(_tabs.CanGoBack(tab.Id));
	}

	[Fact]
	public void The_tab_state_offers_back_and_forward_for_a_woken_tab()
	{
		var id = SleptAndWoken();
		var view = _tabs.State("manual", false).Tabs.Single(t => t.Id == id);
		Assert.True(view.CanGoBack);
		Assert.True(view.CanGoForward);
	}

	[Fact]
	public void Going_on_to_a_new_page_drops_what_was_ahead_and_keeps_what_was_behind()
	{
		var id = SleptAndWoken();
		var page = PageOf(id);
		// A link from c: the engine now has c and the new page.
		page.CanGoBack = true;
		page.Events.StateChanged();
		Assert.False(_tabs.CanGoForward(id));

		_tabs.GoBack(id);
		Assert.Equal("back", page.Calls[^1]);
		page.CanGoBack = false;
		page.CanGoForward = true;
		page.Events.StateChanged();

		// Back from the page's first entry goes on into what it had before it slept.
		_tabs.GoBack(id);
		Assert.Equal("replace https://x.test/b", page.Calls[^1]);
		// Forward comes back to c, in place of b, before the engine's own Forward.
		_tabs.GoForward(id);
		Assert.Equal("replace https://x.test/c", page.Calls[^1]);
		_tabs.GoForward(id);
		Assert.Equal("forward", page.Calls[^1]);
	}

	[Fact]
	public void A_restored_tab_goes_back_and_forward_through_its_saved_history()
	{
		_tabs.Restore([new SessionTab("https://x.test/b", "B", false, true, History: Saved(1, "a", "b", "c"))]);
		var id = _tabs.ActiveId!;
		var page = PageOf(id);
		Assert.Equal("navigate https://x.test/b", page.Calls[0]);
		Assert.True(_tabs.CanGoBack(id));
		_tabs.GoForward(id);
		Assert.Equal("replace https://x.test/c", page.Calls[^1]);
		_tabs.GoBack(id);
		_tabs.GoBack(id);
		Assert.Equal("replace https://x.test/a", page.Calls[^1]);
	}

	[Fact]
	public void A_tab_that_never_slept_uses_the_engine_alone()
	{
		var tab = _tabs.Open("https://x.test/a");
		var page = PageOf(tab.Id);
		Assert.False(_tabs.CanGoBack(tab.Id));
		page.CanGoBack = true;
		_tabs.GoBack(tab.Id);
		Assert.Equal("back", page.Calls[^1]);
	}

	[Fact]
	public void The_history_of_a_sleeping_tab_is_saved_in_the_session_and_comes_back()
	{
		var id = SleptAndWoken();
		_tabs.Open("https://y.test/2");
		_tabs.Sleep(id);
		var text = Session.EncodeWindows([Session.ToSessionTabs(_tabs.Records, _tabs.ActiveId, null, _tabs.HistoryOf)]);
		var saved = Session.DecodeWindows(text).Single().Single(t => t.Url == "https://x.test/c");
		Assert.Equal(2, saved.History!.Index);
		Assert.Equal(["https://x.test/a", "https://x.test/b", "https://x.test/c", "https://x.test/d"], saved.History.Entries.Select(e => e.Url));
		Assert.Equal("A", saved.History.Entries[0].Title);
	}

	[Fact]
	public void The_history_of_an_awake_tab_is_saved_from_its_page()
	{
		var tab = _tabs.Open("https://x.test/b");
		PageOf(tab.Id).History = Engine(1, "a", "b");
		var saved = Session.ToSessionTabs(_tabs.Records, _tabs.ActiveId, null, _tabs.HistoryOf).Single();
		Assert.Equal(["https://x.test/a", "https://x.test/b"], saved.History!.Entries.Select(e => e.Url));
		Assert.Equal(1, saved.History.Index);
	}

	[Fact]
	public void Only_web_pages_of_a_history_are_saved_and_the_index_follows()
	{
		var history = new TabHistory([new("about:blank", ""), new("https://x.test/a", "A"), new("zing-start://new-tab/", ""), new("https://x.test/b", "B")], 3);
		var tab = new SessionTab("https://x.test/b", "B", false, true, History: history);
		var saved = Session.DecodeWindows(Session.EncodeWindows([[tab]])).Single().Single();
		Assert.Equal(["https://x.test/a", "https://x.test/b"], saved.History!.Entries.Select(e => e.Url));
		Assert.Equal(1, saved.History.Index);
	}

	[Fact]
	public void A_single_page_has_no_history_to_save()
	{
		var tab = _tabs.Open("https://x.test/a");
		PageOf(tab.Id).History = Engine(0, "a");
		Assert.Null(Session.ToSessionTabs(_tabs.Records, _tabs.ActiveId, null, _tabs.HistoryOf).Single().History);
	}

	[Theory]
	[InlineData("""{"entries":[{"url":"https://x.test/a","title":"A"}],"index":3}""")]
	[InlineData("""{"entries":[{"url":"https://x.test/a","title":"A"},{"url":"https://x.test/b","title":"B"}],"index":0}""")]
	[InlineData("""{"entries":"no","index":0}""")]
	[InlineData("""[1]""")]
	public void A_malformed_or_mismatched_history_is_dropped_and_the_tab_kept(string history)
	{
		var text = $$"""{"version":2,"windows":[{"tabs":[{"url":"https://x.test/b","title":"B","pinned":false,"active":true,"history":{{history}}}]}]}""";
		var tab = Session.DecodeWindows(text).Single().Single();
		Assert.Equal("https://x.test/b", tab.Url);
		Assert.Null(tab.History);
	}

	[Fact]
	public void A_session_without_histories_still_loads()
	{
		var text = """{"version":2,"windows":[{"tabs":[{"url":"https://x.test/b","title":"B","pinned":false,"active":true}]}]}""";
		Assert.Null(Session.DecodeWindows(text).Single().Single().History);
	}

	[Fact]
	public void Navigating_a_sleeping_tab_puts_where_it_was_behind_and_drops_what_was_ahead()
	{
		var id = SleptAndWoken();
		_tabs.Open("https://y.test/2");
		_tabs.Sleep(id);
		_tabs.Navigate(id, "https://x.test/e");
		var history = _tabs.HistoryOf(id)!;
		Assert.Equal(["https://x.test/a", "https://x.test/b", "https://x.test/c", "https://x.test/e"], history.Entries.Select(e => e.Url));
		Assert.Equal(3, history.Index);
	}

	[Fact]
	public void A_history_is_kept_to_the_entries_nearest_the_current_one()
	{
		var many = new TabHistory([.. Enumerable.Range(0, 120).Select(i => new TabHistoryEntry($"https://x.test/{i}", ""))], 100);
		var kept = many.Kept(Session.IsRestorableUrl, u => u)!;
		Assert.Equal(TabHistory.MaxEntries, kept.Entries.Count);
		Assert.Equal("https://x.test/100", kept.Current.Url);
	}
}

public sealed class UploadTrackerTests
{
	private static string Headers(string id, string length) =>
		JsonSerializer.Serialize(new { requestId = id, headers = new Dictionary<string, string> { ["Content-Type"] = "multipart/form-data", ["Content-Length"] = length } });

	private static string End(string id) => JsonSerializer.Serialize(new { requestId = id });

	[Fact]
	public void A_large_body_counts_until_its_request_finishes()
	{
		var uploads = new UploadTracker();
		uploads.HeadersSent(Headers("1", "5000000"));
		Assert.True(uploads.IsUploading);
		uploads.Ended(End("1"));
		Assert.False(uploads.IsUploading);
	}

	[Fact]
	public void A_small_body_or_none_never_counts()
	{
		var uploads = new UploadTracker();
		uploads.HeadersSent(Headers("1", "512"));
		uploads.HeadersSent(JsonSerializer.Serialize(new { requestId = "2", headers = new Dictionary<string, string> { ["Accept"] = "*/*" } }));
		uploads.HeadersSent(Headers("3", "lots"));
		Assert.False(uploads.IsUploading);
	}

	[Fact]
	public void The_header_name_is_matched_whatever_its_case()
	{
		var uploads = new UploadTracker();
		uploads.HeadersSent(JsonSerializer.Serialize(new { requestId = "1", headers = new Dictionary<string, string> { ["content-length"] = $"{UploadTracker.MinBytes}" } }));
		Assert.True(uploads.IsUploading);
	}

	[Fact]
	public void Each_upload_counts_until_its_own_request_ends()
	{
		var uploads = new UploadTracker();
		uploads.HeadersSent(Headers("1", "5000000"));
		uploads.HeadersSent(Headers("2", "5000000"));
		uploads.Ended(End("1"));
		Assert.True(uploads.IsUploading);
		uploads.Ended(End("unrelated"));
		Assert.True(uploads.IsUploading);
		uploads.Clear();
		Assert.False(uploads.IsUploading);
	}

	[Fact]
	public void An_event_without_a_request_id_is_an_error() =>
		Assert.Throws<InvalidDataException>(() => new UploadTracker().HeadersSent("""{"headers":{}}"""));
}
