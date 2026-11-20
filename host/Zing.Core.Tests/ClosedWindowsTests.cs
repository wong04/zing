using Zing.Core;

namespace Zing.Core.Tests;

public sealed class ClosedWindowsTests
{
	private static IReadOnlyList<SessionTab> Window(string url) => [new SessionTab(url, "t", false, true)];

	[Fact]
	public void The_last_window_closed_comes_back_first_and_only_once()
	{
		var closed = new ClosedWindows();
		closed.Push(Window("https://a.test/"));
		closed.Push(Window("https://b.test/"));
		Assert.Equal("https://b.test/", Assert.Single(closed.Pop()!).Url);
		Assert.Equal("https://a.test/", Assert.Single(closed.Pop()!).Url);
		Assert.Null(closed.Pop());
	}

	[Fact]
	public void A_window_of_new_tabs_is_not_kept()
	{
		var closed = new ClosedWindows();
		closed.Push(Window("zing://home"));
		Assert.Null(closed.Pop());
	}

	[Fact]
	public void Only_the_newest_windows_are_kept()
	{
		var closed = new ClosedWindows();
		for (var i = 0; i <= ClosedWindows.Capacity; i++) closed.Push(Window($"https://w{i}.test/"));
		Assert.Equal(ClosedWindows.Capacity, closed.Count);
		Assert.Equal($"https://w{ClosedWindows.Capacity}.test/", closed.Pop()![0].Url);
	}

	private static IReadOnlyList<SessionTab> Web(int n) => [.. Enumerable.Range(0, n).Select(i => new SessionTab($"https://t{i}.test/", "t", false, i == 0))];

	[Theory]
	[InlineData(1, false, false)]
	[InlineData(1, true, false)]
	[InlineData(5, true, true)]
	public void Closing_with_little_to_lose_asks_nothing(int tabs, bool lastWindow, bool tabsReopen)
	{
		Assert.Null(WindowClose.Question(Web(tabs), lastWindow, tabsReopen));
	}

	[Fact]
	public void Closing_a_window_of_tabs_asks_and_says_how_to_get_it_back()
	{
		Assert.Contains("Ctrl+Shift+T", WindowClose.Question(Web(2), lastWindow: false, tabsReopen: true));
		Assert.Contains("won't reopen", WindowClose.Question(Web(3), lastWindow: true, tabsReopen: false));
	}

	[Fact]
	public void Only_tabs_that_could_come_back_count_so_the_ctrl_shift_t_promise_holds()
	{
		IReadOnlyList<SessionTab> oneWebAndNewTabs = [.. Web(1), new("zing://home", "t", false, false), new("zing://home", "t", false, false)];
		Assert.Null(WindowClose.Question(oneWebAndNewTabs, lastWindow: false, tabsReopen: false));
		IReadOnlyList<SessionTab> twoWebAndANewTab = [.. Web(2), new("zing://home", "t", false, false)];
		Assert.Contains("its 2 tabs", WindowClose.Question(twoWebAndANewTab, lastWindow: false, tabsReopen: false));
		var closed = new ClosedWindows();
		closed.Push(twoWebAndANewTab);
		Assert.Equal(2, closed.Pop()!.Count);
	}

	[Fact]
	public void Quitting_asks_once_for_every_window_and_only_when_tabs_are_lost()
	{
		Assert.Null(WindowClose.QuitQuestion([Web(3), Web(2)], tabsReopen: true));
		Assert.Null(WindowClose.QuitQuestion([Web(1)], tabsReopen: false));
		Assert.Contains("its 2 windows and 5 tabs", WindowClose.QuitQuestion([Web(3), Web(2)], tabsReopen: false));
	}

	[Fact]
	public void Close_requests_that_come_together_are_handed_over_together()
	{
		var scheduler = new ManualScheduler();
		var handed = new List<IReadOnlyList<string>>();
		var burst = new CloseBurst<string>(scheduler, handed.Add);
		burst.Request("a");
		burst.Request("b");
		burst.Request("a");
		Assert.Empty(handed);
		scheduler.Advance(CloseBurst<string>.Wait);
		Assert.Equal(["a", "b"], Assert.Single(handed));
		burst.Request("c");
		scheduler.Advance(CloseBurst<string>.Wait);
		Assert.Equal(["c"], handed[1]);
	}
}
