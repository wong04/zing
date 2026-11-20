using Zing.Core;

namespace Zing.Core.Tests;

/// <summary>F11: the whole window goes to the page, without the chrome or the tab strip, until F11 or Esc again.</summary>
public sealed class BrowserFullScreenTests : IDisposable
{
	private static readonly KeyInput F11 = new("F11", Ctrl: false, Shift: false, Alt: false);
	private static readonly KeyInput Esc = new("Escape", Ctrl: false, Shift: false, Alt: false);

	private readonly FakeWindowHost _host = new();
	private readonly SettingsStore _settings = new();
	private readonly ChromeApi _api;

	public BrowserFullScreenTests()
	{
		var library = new LibraryStore(Library.EmptyLibrary(0), _ => { }, Deterministic.Clock(), Deterministic.Ids("id-"));
		_api = new ChromeApi(new Bridge(), new AppServices(_settings, library, Address.FromDefaults(), new TickingClock(), Accounts.Guest(), new Zing.Core.Net.RelayConsent()), _host, "test");
		_api.Start([], "https://example.com/");
	}

	public void Dispose() => _api.Dispose();

	private FakePage ActivePage => (FakePage)_api.Tabs.ActivePage!;

	[Fact]
	public void F11_takes_the_window_fullscreen_and_a_second_F11_gives_it_back()
	{
		Assert.True(_api.HandleKey(F11));
		Assert.True(_host.BrowserFullScreen);
		Assert.True(_api.HandleChromeKey(F11));
		Assert.False(_host.BrowserFullScreen);
	}

	[Fact]
	public void The_page_takes_the_whole_window_while_it_is_fullscreen()
	{
		var windowed = _api.Insets();
		Assert.True(windowed.Top > 0);
		_api.Run("view.fullScreen");
		Assert.Equal(new ChromeInsets(0, 0, 0), _api.Insets());
		_api.Run("view.fullScreen");
		Assert.Equal(windowed, _api.Insets());
	}

	[Fact]
	public void Esc_leaves_fullscreen_rather_than_stopping_the_page()
	{
		_api.Run("view.fullScreen");
		ActivePage.IsLoading = true;
		Assert.True(_api.HandleKey(Esc));
		Assert.False(_host.BrowserFullScreen);
		Assert.DoesNotContain("stop", ActivePage.Calls);
	}

	[Fact]
	public void Esc_in_a_chrome_view_leaves_fullscreen_too()
	{
		_api.Run("view.fullScreen");
		Assert.True(_api.HandleChromeKey(Esc));
		Assert.False(_host.BrowserFullScreen);
	}

	[Fact]
	public void Esc_is_the_page_s_while_one_of_its_elements_is_fullscreen()
	{
		_api.Run("view.fullScreen");
		ActivePage.IsFullScreen = true;
		Assert.False(_api.HandleKey(Esc));
		Assert.True(_host.BrowserFullScreen);
	}

	[Fact]
	public void The_View_menu_offers_it_with_its_key()
	{
		var view = Menus.App(_ => { }).Single(m => m.Label == "View");
		var item = view.Submenu!.Single(m => m.Label == "Full Screen");
		Assert.Equal("F11", item.Accelerator);
	}

	[Fact]
	public void The_hint_says_how_to_leave()
	{
		Assert.Equal("Full screen · Press F11 or Esc to exit", FullScreenHint.Browser);
	}
}
