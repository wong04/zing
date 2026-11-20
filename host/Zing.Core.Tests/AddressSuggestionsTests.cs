using System.Text.Json;
using Zing.Core;

namespace Zing.Core.Tests;

public sealed class AddressRankingTests
{
	private static TabRecord Tab(string id, string title, string url) => new(id, url, title, false, 0, 0, 0);

	private static BrowserLibrary LibraryOf(IReadOnlyList<BookmarkNode> favourites, IReadOnlyList<ReadingItem>? reading = null) =>
		new([Library.Folder(Library.FavouritesId, Library.FavouritesTitle, favourites, 0), Library.Folder(Library.PinnedId, Library.PinnedTitle, [], 0)], reading ?? []);

	private static readonly BrowserLibrary Empty = LibraryOf([]);

	private static IReadOnlyList<string> Urls(IReadOnlyList<AddressSuggestion> items) => [.. items.Select(i => i.Url)];

	[Fact]
	public void Blank_text_suggests_nothing()
	{
		Assert.Empty(AddressRanking.Rank("   ", [Tab("a", "Anything", "https://a.example/")], null, Empty));
	}

	[Fact]
	public void The_start_of_the_host_beats_the_start_of_a_title_word_beats_anywhere()
	{
		var library = LibraryOf([
			Library.Link("1", "Notes on things", "https://example.com/git-notes", 0),
			Library.Link("2", "Git cheatsheet", "https://example.com/cheats", 0),
			Library.Link("3", "Code hosting", "https://github.com/", 0),
		]);
		Assert.Equal(["https://github.com/", "https://example.com/cheats", "https://example.com/git-notes"], Urls(AddressRanking.Rank("git", [], null, library)));
	}

	[Fact]
	public void The_scheme_and_www_dont_count_as_the_start_of_the_host()
	{
		var library = LibraryOf([Library.Link("1", "Wiki", "https://www.wikipedia.org/", 0)]);
		Assert.Single(AddressRanking.Rank("wikip", [], null, library));
	}

	[Fact]
	public void Every_word_must_match_and_case_is_ignored()
	{
		var library = LibraryOf([
			Library.Link("1", "React docs", "https://react.dev/learn", 0),
			Library.Link("2", "React blog", "https://react.dev/blog", 0),
		]);
		Assert.Equal(["https://react.dev/learn"], Urls(AddressRanking.Rank("REACT Docs", [], null, library)));
	}

	[Fact]
	public void On_a_tie_open_tabs_come_before_bookmarks_before_the_Reading_List()
	{
		var library = LibraryOf([Library.Link("b", "Mail bookmark", "https://mail.example/b", 0)], [Library.Reading("r", "Mail article", "https://mail.example/r", 0)]);
		var items = AddressRanking.Rank("mail", [Tab("t", "Mail tab", "https://mail.example/t")], null, library);
		Assert.Equal([SuggestionKinds.Tab, SuggestionKinds.Bookmark, SuggestionKinds.Reading], items.Select(i => i.Kind));
		Assert.Equal("t", items[0].TabId);
		Assert.Null(items[1].TabId);
	}

	[Fact]
	public void An_open_tab_replaces_its_own_bookmark_and_the_tab_on_screen_is_left_out()
	{
		var library = LibraryOf([Library.Link("b", "Docs", "https://docs.example/", 0)]);
		var tabs = new[] { Tab("on-screen", "Docs home", "https://docs.example/home"), Tab("other", "Docs", "https://docs.example/") };
		var items = AddressRanking.Rank("docs", tabs, "on-screen", library);
		Assert.Equal([new AddressSuggestion(SuggestionKinds.Tab, "Docs", "https://docs.example/", "other")], items);
	}

	[Fact]
	public void Bookmarks_in_folders_count_and_addresses_Zing_cant_open_are_left_out()
	{
		var library = LibraryOf([
			Library.Folder("f", "Work", [Library.Link("1", "Shop deep", "https://shop.example/deep", 0)], 0),
			Library.Link("2", "Shop relay", "zing-relay://shop.example/", 0),
			Library.Link("3", "Shop script", "javascript:alert(1)", 0),
		]);
		Assert.Equal(["https://shop.example/deep"], Urls(AddressRanking.Rank("shop", [], null, library)));
	}

	[Fact]
	public void At_most_eight_are_suggested()
	{
		var links = Enumerable.Range(0, 12).Select(i => (BookmarkNode)Library.Link($"{i}", $"Item {i}", $"https://item{i}.example/", 0)).ToList();
		Assert.Equal(AddressRanking.Max, AddressRanking.Rank("item", [], null, LibraryOf(links)).Count);
		Assert.Equal(8, AddressRanking.Max);
	}
}

public sealed class AddressSuggestionsChannelTests : IDisposable
{
	private readonly Bridge _bridge = new();
	private readonly FakeWindowHost _host = new();
	private readonly LibraryStore _library;
	private readonly ChromeApi _api;
	private static readonly object Field = new { x = 120, y = 40, width = 600, height = 28 };

	public AddressSuggestionsChannelTests()
	{
		_library = new LibraryStore(Library.EmptyLibrary(0), _ => { }, Deterministic.Clock(), Deterministic.Ids("id-"));
		_api = new ChromeApi(_bridge, new AppServices(new SettingsStore(), _library, Address.FromDefaults(), new TickingClock(), Accounts.Guest(), new Zing.Core.Net.RelayConsent(), BrowsingData: new FakeBrowsingData()), _host, "test");
		_api.Start([], "about:blank");
	}

	public void Dispose() => _api.Dispose();

	private async Task<JsonElement> Invoke(string channel, params object?[] args)
	{
		var json = JsonSerializer.Serialize(new { kind = "invoke", id = 1, channel, args });
		var result = JsonDocument.Parse(await _bridge.HandleAsync(json)).RootElement;
		if (!result.GetProperty("ok").GetBoolean()) throw new Xunit.Sdk.XunitException(result.GetProperty("error").ToString());
		return result.TryGetProperty("value", out var value) ? value : default;
	}

	private (string, object?) LastPush() => _host.Pushed.Last(p => p.Item1 == "addressbar:suggestions");

	[Fact]
	public async Task Suggest_answers_the_ranked_rows_and_shows_them_under_the_field()
	{
		_library.AddLink(Library.FavouritesId, 0, "News", "https://news.example/");
		var rows = await Invoke("addressbar:suggest", "new", Field);
		Assert.Equal("https://news.example/", rows[0].GetProperty("url").GetString());
		Assert.Equal("bookmark", rows[0].GetProperty("kind").GetString());
		var shown = _host.Suggestions[^1]!;
		Assert.Equal(new Rect(120, 40, 600, 28), shown.Field);
		Assert.Equal(-1, shown.Active);
		Assert.Same(shown, LastPush().Item2);
	}

	[Fact]
	public async Task Nothing_matching_closes_the_list()
	{
		_library.AddLink(Library.FavouritesId, 0, "News", "https://news.example/");
		await Invoke("addressbar:suggest", "new", Field);
		Assert.Equal(0, (await Invoke("addressbar:suggest", "zzz", Field)).GetArrayLength());
		Assert.Null(_host.Suggestions[^1]);
		Assert.Null(_api.Suggestions.Current);
	}

	[Fact]
	public async Task The_arrows_highlight_moves_and_out_of_range_is_refused()
	{
		_library.AddLink(Library.FavouritesId, 0, "News", "https://news.example/");
		await Invoke("addressbar:suggest", "new", Field);
		await Invoke("addressbar:highlight", 0);
		Assert.Equal(0, _host.Suggestions[^1]!.Active);
		await Invoke("addressbar:highlight", -1);
		Assert.Equal(-1, _host.Suggestions[^1]!.Active);
		await Assert.ThrowsAsync<Xunit.Sdk.XunitException>(() => Invoke("addressbar:highlight", 1));
		await Invoke("addressbar:close");
		Assert.Null(_host.Suggestions[^1]);
		await Assert.ThrowsAsync<Xunit.Sdk.XunitException>(() => Invoke("addressbar:highlight", 0));
	}

	[Fact]
	public async Task Picking_an_open_tab_switches_to_it_and_closes_the_list()
	{
		var first = _api.Tabs.ActiveId!;
		await Invoke("tabs:navigate", first, "https://first.example/");
		await Invoke("tabs:open-link", "https://second.example/");
		Assert.NotEqual(first, _api.Tabs.ActiveId);
		var rows = await Invoke("addressbar:suggest", "first", Field);
		Assert.Equal(first, rows[0].GetProperty("tabId").GetString());
		var before = _api.Tabs.Records.Count;
		await Invoke("addressbar:open", rows[0]);
		Assert.Equal(first, _api.Tabs.ActiveId);
		Assert.Equal(before, _api.Tabs.Records.Count);
		Assert.Null(_api.Suggestions.Current);
	}

	[Fact]
	public async Task Picking_a_bookmark_opens_it_in_the_tab_on_screen()
	{
		_library.AddLink(Library.FavouritesId, 0, "News", "https://news.example/");
		var active = _api.Tabs.ActiveId!;
		var rows = await Invoke("addressbar:suggest", "news", Field);
		await Invoke("addressbar:open", rows[0]);
		Assert.Equal(active, _api.Tabs.ActiveId);
		Assert.Contains("navigate https://news.example/", ((FakePage)_api.Tabs.ActivePage!).Calls);
		Assert.Null(_api.Suggestions.Current);
	}

	[Fact]
	public async Task An_unknown_kind_is_refused()
	{
		await Assert.ThrowsAsync<Xunit.Sdk.XunitException>(() => Invoke("addressbar:open", new { kind = "history", title = "x", url = "https://x.example/" }));
	}
}
