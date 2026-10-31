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
		_api = new ChromeApi(new Bridge(), new AppServices(_settings, library, Address.FromDefaults(), Deterministic.Clock()), _host, "test");
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
