using System.Text.Json;
using Zing.Core;

namespace Zing.Core.Tests;

public sealed class ChromeApiTests : IDisposable
{
	private readonly Bridge _bridge = new();
	private readonly FakeWindowHost _host = new();
	private readonly SettingsStore _settings = new();
	private readonly List<BrowserLibrary> _saved = [];
	private readonly FakeBrowsingData _browsingData = new();
	private readonly ChromeApi _api;

	public ChromeApiTests()
	{
		var library = new LibraryStore(Library.EmptyLibrary(0), _saved.Add, Deterministic.Clock(), Deterministic.Ids("id-"));
		_api = new ChromeApi(_bridge, new AppServices(_settings, library, Address.FromDefaults(), new TickingClock(), Accounts.Guest(), BrowsingData: _browsingData), _host, "test");
		_api.Start([], "about:blank");
	}

	public void Dispose() => _api.Dispose();

	private string ActiveId => _api.Tabs.ActiveId!;
	private FakePage ActivePage => (FakePage)_api.Tabs.ActivePage!;

	private async Task<JsonElement> Invoke(string channel, params object?[] args)
	{
		var json = JsonSerializer.Serialize(new { kind = "invoke", id = 1, channel, args });
		return JsonDocument.Parse(await _bridge.HandleAsync(json)).RootElement;
	}

	/// <summary>The result's value; a handler that returns nothing sends no `value`, which the renderer reads as undefined.</summary>
	private static JsonElement Value(JsonElement result)
	{
		if (!result.GetProperty("ok").GetBoolean()) throw new Xunit.Sdk.XunitException(result.GetProperty("error").ToString());
		return result.TryGetProperty("value", out var value) ? value : default;
	}

	private static string ErrorName(JsonElement result) => result.GetProperty("error").GetProperty("name").GetString()!;

	[Fact]
	public async Task Clear_session_clears_the_profile_and_says_so_and_keeps_the_tabs()
	{
		Value(await Invoke("tabs:navigate", ActiveId, "https://example.com"));
		Value(await Invoke("shell:clear-session"));
		Assert.Equal(1, _browsingData.Clears);
		Assert.Equal("Browsing session cleared", _host.Notices[^1]);
		Assert.Contains("example.com", Value(await Invoke("tabs:get")).GetProperty("tabs")[0].GetProperty("url").GetString());
	}

	[Fact]
	public async Task Clear_session_that_fails_rejects_and_claims_nothing()
	{
		_browsingData.Failure = new IOException("engine gone");
		Assert.Equal(nameof(IOException), ErrorName(await Invoke("shell:clear-session")));
		Assert.DoesNotContain("Browsing session cleared", _host.Notices);
	}

	[Fact]
	public async Task Clear_session_without_an_engine_rejects()
	{
		var bridge = new Bridge();
		var library = new LibraryStore(Library.EmptyLibrary(0), _ => { }, Deterministic.Clock(), Deterministic.Ids("id-"));
		using var api = new ChromeApi(bridge, new AppServices(new SettingsStore(), library, Address.FromDefaults(), new TickingClock(), Accounts.Guest()), new FakeWindowHost(), "test");
		var json = JsonSerializer.Serialize(new { kind = "invoke", id = 1, channel = "shell:clear-session", args = Array.Empty<object>() });
		Assert.Equal(nameof(InvalidOperationException), ErrorName(JsonDocument.Parse(await bridge.HandleAsync(json)).RootElement));
	}

	[Fact]
	public async Task Typed_text_is_resolved_and_loaded_in_that_tab()
	{
		Value(await Invoke("tabs:navigate", ActiveId, "hello world"));
		Assert.Equal(["navigate https://www.google.com/search?q=hello%20world", "focus"], ActivePage.Calls[^2..]);
	}

	[Fact]
	public async Task Blank_input_does_nothing()
	{
		var before = ActivePage.Calls.Count;
		Value(await Invoke("tabs:navigate", ActiveId, "   "));
		Assert.Equal(before, ActivePage.Calls.Count);
	}

	[Fact]
	public async Task A_url_Zing_cannot_open_is_a_notice_not_a_navigation()
	{
		Value(await Invoke("tabs:navigate", ActiveId, "ftp://example.com/file"));
		Assert.Contains(_host.Notices, n => n.StartsWith("Zing can't open", StringComparison.Ordinal));
	}

	[Fact]
	public async Task Navigating_an_unknown_tab_is_an_error()
	{
		Assert.Equal(nameof(UnknownTabException), ErrorName(await Invoke("tabs:navigate", "nope", "example.com")));
	}

	[Fact]
	public async Task New_tab_and_close_go_through_the_bridge_and_push_the_strip()
	{
		Value(await Invoke("command:run", "tab.new"));
		Assert.Equal(2, Value(await Invoke("tabs:get")).GetProperty("tabs").GetArrayLength());
		Assert.Contains(_host.Pushed, p => p.Channel == "chrome:focus-address");
		Value(await Invoke("tabs:close", ActiveId));
		Assert.Equal(1, Value(await Invoke("tabs:get")).GetProperty("tabs").GetArrayLength());
		Assert.Contains(_host.Pushed, p => p.Channel == "tabs:state");
	}

	[Fact]
	public async Task Back_is_done_only_when_there_is_history()
	{
		Assert.Equal("not-applicable", Value(await Invoke("command:run", "history.back")).GetProperty("status").GetString());
		ActivePage.CanGoBack = true;
		Assert.Equal("done", Value(await Invoke("command:run", "history.back")).GetProperty("status").GetString());
		Assert.Equal("back", ActivePage.Calls[^1]);
	}

	[Fact]
	public async Task Every_command_in_the_registry_has_a_handler()
	{
		foreach (var command in Commands.All.Where(c => c.Id != "window.close"))
			Assert.NotEqual("not-implemented", Value(await Invoke("command:run", command.Id)).GetProperty("status").GetString());
		Assert.Equal(nameof(UnknownCommandException), ErrorName(await Invoke("command:run", "no.such.command")));
	}

	[Fact]
	public async Task Text_size_steps_per_tab()
	{
		Value(await Invoke("command:run", "page.textBigger"));
		Assert.Equal(110, ActivePage.TextSize);
	}

	[Fact]
	public async Task Zoom_commands_step_the_tab_and_actual_size_resets_it()
	{
		Assert.Equal("done", Value(await Invoke("command:run", "page.zoomIn")).GetProperty("status").GetString());
		Assert.Equal(1.1, ActivePage.Zoom);
		Value(await Invoke("command:run", "page.zoomIn"));
		Assert.Equal(1.25, ActivePage.Zoom);
		Value(await Invoke("command:run", "page.zoomOut"));
		Value(await Invoke("command:run", "page.zoomOut"));
		Value(await Invoke("command:run", "page.zoomOut"));
		Assert.Equal(0.9, ActivePage.Zoom);
		Value(await Invoke("command:run", "page.actualSize"));
		Assert.Equal(1.0, ActivePage.Zoom);
	}

	[Fact]
	public async Task Reader_says_why_a_page_has_no_article_and_a_second_toggle_reloads()
	{
		ActivePage.ReaderRefusal = "Reader can't find an article on this page";
		Value(await Invoke("command:run", "page.reader"));
		Assert.Equal(["Reader can't find an article on this page"], _host.Notices);
		ActivePage.ReaderRefusal = null;
		Value(await Invoke("command:run", "page.reader"));
		Value(await Invoke("command:run", "page.reader"));
		Assert.Equal("reload", ActivePage.Calls[^1]);
	}

	[Fact]
	public async Task A_setting_is_validated_stored_and_pushed()
	{
		Value(await Invoke("settings:set", "searchEngine", "duckduckgo"));
		Assert.Equal("duckduckgo", _settings.Get("searchEngine")!.GetValue<string>());
		Assert.Contains(_host.Pushed, p => p.Channel == "settings:changed");
		Assert.Equal(nameof(InvalidSettingException), ErrorName(await Invoke("settings:set", "searchEngine", "altavista")));
		Assert.Equal(nameof(UnknownSettingException), ErrorName(await Invoke("settings:set", "nope", true)));
	}

	[Fact]
	public async Task Pinning_the_sidebar_moves_the_page_and_relayouts()
	{
		var pinned = _api.Insets().Left;
		Value(await Invoke("command:run", "view.sidebar"));
		Assert.NotEqual(pinned, _api.Insets().Left);
		Assert.True(_host.Relayouts > 0);
	}

	[Fact]
	public async Task A_favourite_is_saved_from_the_active_page()
	{
		Assert.Equal("not-applicable", Value(await Invoke("command:run", "library.addFavourite")).GetProperty("status").GetString());
		Value(await Invoke("tabs:navigate", ActiveId, "https://example.com"));
		ActivePage.Events.TitleChanged("Example");
		Value(await Invoke("command:run", "library.addFavourite"));
		Assert.Equal(["Added to Favourites."], _host.Notices);
		var library = Value(await Invoke("library:get"));
		Assert.Equal("https://example.com", library.GetProperty("library").GetProperty("bookmarks")[0].GetProperty("children")[0].GetProperty("url").GetString());
		Assert.NotEmpty(_saved);
	}

	[Fact]
	public async Task The_library_panel_takes_room_from_the_left()
	{
		var before = _api.Insets().Left;
		Value(await Invoke("library:panel", "bookmarks"));
		Assert.Equal(before + Layout.LibraryPanelWidth, _api.Insets().Left);
		Assert.Equal("bookmarks", Value(await Invoke("library:get")).GetProperty("panel").GetString());
	}

	[Fact]
	public async Task Settings_open_and_close_through_the_shell_state()
	{
		Value(await Invoke("command:run", "app.settings"));
		Assert.True(Value(await Invoke("shell:get")).GetProperty("settingsOpen").GetBoolean());
		Value(await Invoke("shell:close-settings"));
		Assert.False(_api.SettingsOpen);
	}

	[Fact]
	public void A_link_from_another_app_reuses_the_lone_blank_tab()
	{
		_api.OpenLink("https://example.com/?utm_source=x");
		var tab = Assert.Single(_api.Tabs.Records);
		Assert.Equal("https://example.com/", tab.Url);
		_api.OpenLink("https://two.example/");
		Assert.Equal(2, _api.Tabs.Records.Count);
		Assert.Throws<UnsupportedUrlException>(() => _api.OpenLink("ms-settings:"));
	}

	[Fact]
	public async Task New_window_and_close_window_go_to_the_host()
	{
		Value(await Invoke("command:run", "window.new"));
		Value(await Invoke("command:run", "window.close"));
		Assert.Equal((1, 1), (_host.NewWindows, _host.Closes));
	}

	[Fact]
	public void Insets_follow_the_tab_bar_position_and_the_rail_width()
	{
		_settings.Set("showFavouritesBar", false);
		_settings.Set("tabBarPosition", "top");
		Assert.Equal(new ChromeInsets(80, Layout.SidebarColumnWidth, 0), _api.Insets());
		// On the left the app sidebar folds into the rail: one column, the rail's width.
		_settings.Set("tabBarPosition", "left");
		Assert.Equal(new ChromeInsets(40, Layout.RailDefaultWidth, 0), _api.Insets());
		_settings.Set("verticalTabWidth", 300);
		Assert.Equal(new ChromeInsets(40, 300, 0), _api.Insets());
		// Hidden (Ctrl+S), the rail gives the page its column and slides out from the edge instead.
		_settings.Set("sidebarPinned", false);
		Assert.Equal(new ChromeInsets(40, 0, 0), _api.Insets());
		// On the right the rail always shows; the app sidebar keeps its own column on the left.
		_settings.Set("tabBarPosition", "right");
		Assert.Equal(new ChromeInsets(40, 0, 300), _api.Insets());
		_settings.Set("sidebarPinned", true);
		Assert.Equal(new ChromeInsets(40, Layout.SidebarColumnWidth, 300), _api.Insets());
	}

	[Fact]
	public void The_sidebar_that_slides_out_is_as_wide_as_the_rail_when_tabs_are_on_the_left()
	{
		_settings.Set("tabBarPosition", "top");
		Assert.Equal(Layout.SidebarColumnWidth, _api.Shell.SidebarWidth);
		_settings.Set("tabBarPosition", "left");
		_settings.Set("verticalTabWidth", 320);
		Assert.Equal(320, _api.Shell.SidebarWidth);
		Assert.Equal(320, _api.Shell.PinnedWidth);
		_settings.Set("sidebarPinned", false);
		Assert.Equal(0, _api.Shell.PinnedWidth);
	}
}
