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

	[Theory]
	[InlineData(1, false, false)]
	[InlineData(1, true, false)]
	[InlineData(5, true, true)]
	public void Closing_with_little_to_lose_asks_nothing(int tabs, bool lastWindow, bool tabsReopen)
	{
		Assert.Null(WindowClose.Question(tabs, lastWindow, tabsReopen));
	}

	[Fact]
	public void Closing_a_window_of_tabs_asks_and_says_how_to_get_it_back()
	{
		Assert.Contains("Ctrl+Shift+T", WindowClose.Question(2, lastWindow: false, tabsReopen: true));
		Assert.Contains("won't reopen", WindowClose.Question(3, lastWindow: true, tabsReopen: false));
	}
}
