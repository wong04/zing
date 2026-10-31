using Zing.Core;

namespace Zing.Core.Tests;

// Mirrors src/main/history-menu.test.ts.
public sealed class HistoryMenuTests
{
	private static readonly List<HistoryEntry> Entries = [.. new[] { "a", "b", "c", "d", "e" }.Select((t, i) => new HistoryEntry(i, $"https://x.test/{t}", t))];

	[Fact]
	public void Back_entries_nearest_first() => Assert.Equal(["c", "b", "a"], HistoryMenu.Items(Entries, 3, "back").Select(i => i.Label));

	[Fact]
	public void Forward_entries_nearest_first() => Assert.Equal([2, 3, 4], HistoryMenu.Items(Entries, 1, "forward").Select(i => i.Index));

	[Fact]
	public void Empty_at_the_ends()
	{
		Assert.Empty(HistoryMenu.Items(Entries, 0, "back"));
		Assert.Empty(HistoryMenu.Items(Entries, 4, "forward"));
	}

	[Fact]
	public async Task The_back_menu_lists_earlier_pages_and_goes_to_the_chosen_one()
	{
		var host = new FakeWindowHost();
		var library = new LibraryStore(Library.EmptyLibrary(0), _ => { }, Deterministic.Clock(), Deterministic.Ids("id-"));
		var bridge = new Bridge();
		using var api = new ChromeApi(bridge, new AppServices(new SettingsStore(), library, Address.FromDefaults(), new TickingClock(), Accounts.Guest()), host, "test");
		api.Start([], "https://x.test/e");
		var page = (FakePage)api.Tabs.ActivePage!;
		page.History = new PageHistory(Entries, 4);
		var json = System.Text.Json.JsonSerializer.Serialize(new { kind = "invoke", id = 1, channel = "chrome:history-menu", args = new object[] { "back", new { x = 10, y = 20 } } });
		await bridge.HandleAsync(json);
		var menu = Assert.Single(host.Menus);
		Assert.Equal(["d", "c", "b", "a"], menu.Select(m => m.Label));
		menu[1].Click!();
		Assert.Equal("history 2", page.Calls[^1]);
	}

	[Fact]
	public void Falls_back_to_the_url_and_caps_the_list()
	{
		var many = Enumerable.Range(0, 40).Select(i => new HistoryEntry(i, $"https://x.test/{i}", "")).ToList();
		var items = HistoryMenu.Items(many, 39, "back", 5);
		Assert.Equal(5, items.Count);
		Assert.Equal("https://x.test/38", items[0].Label);
	}
}
