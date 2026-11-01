using Zing.Core;

namespace Zing.Core.Tests;

public sealed class CommandsTests : IDisposable
{
	private readonly FakeWindowHost _host = new();
	private readonly SettingsStore _settings = new();
	private readonly ChromeApi _api;

	public CommandsTests()
	{
		var library = new LibraryStore(Library.EmptyLibrary(0), _ => { }, Deterministic.Clock(), Deterministic.Ids("id-"));
		_api = new ChromeApi(new Bridge(), new AppServices(_settings, library, Address.FromDefaults(), new TickingClock(), Accounts.Guest()), _host, "test");
		_api.Start([], "https://example.com/");
	}

	public void Dispose() => _api.Dispose();

	[Theory]
	[InlineData(0x54, "KeyT")]
	[InlineData(0x31, "Digit1")]
	[InlineData(0x74, "F5")]
	[InlineData(0xBB, "Equal")]
	[InlineData(0x1B, "Escape")]
	public void Virtual_keys_map_to_keyboard_event_codes(int virtualKey, string code) => Assert.Equal(code, Commands.CodeOf(virtualKey));

	[Fact]
	public void A_key_no_shortcut_uses_has_no_code() => Assert.Null(Commands.CodeOf(0x5B));

	[Fact]
	public void Ctrl_T_runs_New_Tab_and_is_taken_from_the_page()
	{
		Assert.True(_api.HandleKey(new KeyInput("KeyT", Ctrl: true, Shift: false, Alt: false)));
		Assert.Equal(2, _api.Tabs.Records.Count);
	}

	[Fact]
	public void A_command_with_nothing_to_act_on_lets_the_key_through()
	{
		Assert.False(_api.HandleKey(new KeyInput("ArrowLeft", Ctrl: false, Shift: false, Alt: true)));
	}

	[Fact]
	public void A_key_without_a_command_is_not_taken()
	{
		Assert.False(_api.HandleKey(new KeyInput("KeyQ", Ctrl: false, Shift: false, Alt: false)));
	}

	[Fact]
	public void The_app_menu_has_the_shared_sections_with_shortcuts_shown()
	{
		var menu = Menus.App(_ => { });
		Assert.Equal(["File", "Edit", "View"], menu.Take(3).Select(m => m.Label));
		var newTab = menu[0].Submenu![0];
		Assert.Equal(("New Tab", "Ctrl+T"), (newTab.Label, newTab.Accelerator));
	}

	[Fact]
	public void The_tab_menu_pins_copies_and_disables_closing_a_pinned_tab()
	{
		var id = _api.Tabs.ActiveId!;
		var menu = Menus.Tab(_api.Tabs, id, _host.Shell);
		menu.Single(m => m.Label == "Copy Address").Click!();
		Assert.Equal(["https://example.com/"], _host.FakeShell.Copied);
		menu.Single(m => m.Label == "Pin Tab").Click!();
		Assert.False(Menus.Tab(_api.Tabs, id, _host.Shell).Single(m => m.Label == "Close Tab").Enabled);
		Assert.Contains(Menus.Tab(_api.Tabs, id, _host.Shell), m => m.Label == "Unpin Tab");
	}
}

public sealed class FindTests : IDisposable
{
	private readonly FakeWindowHost _host = new();
	private readonly ChromeApi _api;

	public FindTests()
	{
		var library = new LibraryStore(Library.EmptyLibrary(0), _ => { }, Deterministic.Clock(), Deterministic.Ids("id-"));
		_api = new ChromeApi(new Bridge(), new AppServices(new SettingsStore(), library, Address.FromDefaults(), new TickingClock(), Accounts.Guest()), _host, "test");
		_api.Start([], "https://example.com/");
	}

	public void Dispose() => _api.Dispose();

	private FakePage Page => (FakePage)_api.Tabs.ActivePage!;

	[Fact]
	public void Find_opens_the_bar_searches_the_page_and_shows_its_results()
	{
		_api.Run("page.find");
		Assert.NotNull(_host.PageUiStates[^1].Find);
		_api.Find.Update("zing");
		Assert.Equal("find zing Next Restart", Page.Calls[^1]);
		Page.Events.FindResult(3, 1);
		Assert.Equal(new FindState("zing", 1, 3), _api.PageUi.State.Find);
		_api.Run("page.findNext");
		Assert.Equal("find zing Next Continue", Page.Calls[^1]);
	}

	[Fact]
	public void Switching_tabs_closes_the_bar_and_clears_the_old_page()
	{
		_api.Run("page.find");
		_api.Find.Update("zing");
		var old = Page;
		_api.Run("tab.new");
		Assert.Null(_api.PageUi.State.Find);
		Assert.Equal("stop find", old.Calls[^1]);
	}
}

public sealed class StackDropdownTests : IDisposable
{
	private readonly FakeWindowHost _host = new();
	private readonly SettingsStore _settings = new();
	private readonly ChromeApi _api;

	public StackDropdownTests()
	{
		var library = new LibraryStore(Library.EmptyLibrary(0), _ => { }, Deterministic.Clock(), Deterministic.Ids("id-"));
		_api = new ChromeApi(new Bridge(), new AppServices(_settings, library, Address.FromDefaults(), new TickingClock(), Accounts.Guest()), _host, "test");
		_api.Start([], "https://news.example/a");
		_api.Tabs.Open("https://news.example/b");
		_api.Tabs.Open("https://other.test/");
		_settings.Set("tabDropdowns", true);
	}

	public void Dispose() => _api.Dispose();

	private string StackKey => _api.Ordering.Stacks().Single(s => s.Ids.Count > 1).Key;

	[Fact]
	public void A_stack_opens_with_its_pages_and_the_strip_knows_which_is_open()
	{
		_api.Stacks.Toggle(StackKey, 120);
		var state = _host.Dropdowns[^1]!;
		Assert.Equal(2, state.Tabs.Count);
		Assert.Equal(StackKey, _api.State().OpenStack);
		_api.Stacks.Toggle(StackKey, 120);
		Assert.Null(_host.Dropdowns[^1]);
	}

	[Fact]
	public void The_dropdown_closes_once_its_stack_has_fewer_than_two_pages()
	{
		var key = StackKey;
		_api.Stacks.Toggle(key, 0);
		_api.Tabs.Close(_api.Tabs.Records.First(r => r.Url == "https://news.example/a").Id);
		Assert.Null(_api.Stacks.OpenKey);
	}

	[Fact]
	public void Opened_by_a_drag_it_closes_soon_after_the_drag_leaves_chip_and_list()
	{
		_api.Stacks.OpenForDrag(StackKey, 0);
		_api.Stacks.DragPresence("chip", false);
		_host.FakeScheduler.Advance(TimeSpan.FromMilliseconds(400));
		Assert.Null(_api.Stacks.OpenKey);
	}
}

public sealed class AccountGateTests
{
	[Fact]
	public async Task Skip_for_now_takes_the_gate_off_the_window()
	{
		var host = new FakeWindowHost();
		var library = new LibraryStore(Library.EmptyLibrary(0), _ => { }, Deterministic.Clock(), Deterministic.Ids("id-"));
		var account = new AccountStore(Directory.CreateTempSubdirectory("zing-account-").FullName, new FakeCipher());
		var bridge = new Bridge();
		using var api = new ChromeApi(bridge, new AppServices(new SettingsStore(), library, Address.FromDefaults(), new TickingClock(), account), host, "test");
		api.Start([], "about:blank");
		Assert.NotNull(host.SurfaceRegions[^1]);
		await bridge.HandleAsync(System.Text.Json.JsonSerializer.Serialize(new { kind = "invoke", id = 1, channel = "account:continue-as-guest", args = Array.Empty<object>() }));
		Assert.False(api.Shell.Gated);
		Assert.Null(host.SurfaceRegions[^1]);
	}
}
