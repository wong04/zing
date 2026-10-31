using System.Text.Json;
using Zing.Core;

namespace Zing.Core.Tests;

public sealed class ShellModelTests
{
	private sealed class Harness
	{
		public ManualScheduler Clock { get; } = new();
		public Point? Cursor { get; set; }
		public Rect Page { get; set; } = new(204, 80, 1000, 700);
		public Point Origin { get; set; } = new(100, 50);
		public List<ShellState> States { get; } = [];
		public List<ToolPanelsState> Panels { get; } = [];
		public List<Rect?> Regions { get; } = [];
		public List<string> Notices { get; } = [];
		public List<IReadOnlyList<string>> Recents { get; } = [];
		public List<string> Events { get; } = [];
		public ShellModel Model { get; }

		public Harness(bool pinned = true, bool gated = false, IReadOnlyList<string>? recents = null)
		{
			var window = new ShellWindow(() => Page, () => new ContentSize(1280, 780), () => Origin, () => Cursor);
			Model = new ShellModel("1.2.3", window, Clock, pinned, gated, recents ?? []);
			Model.StateChanged += States.Add;
			Model.PanelsChanged += Panels.Add;
			Model.SurfaceRegionChanged += Regions.Add;
			Model.NoticeRaised += Notices.Add;
			Model.RecentFeaturesChanged += Recents.Add;
			Model.FocusSurfaceRequested += () => Events.Add("focus surface");
			Model.FocusPageRequested += () => Events.Add("focus page");
			Model.RelayoutRequested += () => Events.Add("relayout");
			Model.SidebarPinRequested += pin => Events.Add($"pin {pin}");
			Model.Start();
		}

		public void Ticks(int count)
		{
			for (var i = 0; i < count; i++) Clock.Advance(SidebarReveal.PollInterval);
		}
	}

	private static string Json(object value) => JsonSerializer.Serialize(value, ZingJson.Options);

	[Fact]
	public void Starts_quiet_with_the_default_state()
	{
		var h = new Harness();
		Assert.Equal(new ShellState(false, "general", false, false, new Point(0, 0), "1.2.3"), h.Model.State());
		Assert.Null(h.Model.Region);
		Assert.Empty(h.Model.PanelsState().Panels);
		Assert.Null(h.Model.PanelsState().FrontId);
		Assert.Equal(204, h.Model.PinnedWidth);
	}

	[Fact]
	public void The_state_serialises_for_the_bridge()
	{
		var h = new Harness();
		h.Model.OpenSettings("privacy");
		Assert.Equal(
			"""{"settingsOpen":true,"settingsCategory":"privacy","sidebarRevealed":false,"gated":false,"origin":{"x":0,"y":0},"appVersion":"1.2.3"}""",
			JsonSerializer.Serialize(h.Model.State(), ZingJson.Options));
	}

	[Fact]
	public void Opening_settings_covers_the_window_pushes_state_and_takes_focus()
	{
		var h = new Harness();
		h.Model.OpenSettings("privacy");
		Assert.Equal(new Rect(0, 0, 1280, 780), h.Model.Region);
		Assert.Equal(new Rect(0, 0, 1280, 780), h.Regions[^1]);
		Assert.True(h.States[^1].SettingsOpen);
		Assert.Equal("privacy", h.States[^1].SettingsCategory);
		Assert.Equal(["focus surface"], h.Events);
	}

	[Fact]
	public void Settings_keep_the_last_category_when_none_is_named()
	{
		var h = new Harness();
		h.Model.OpenSettings("tabs");
		h.Model.CloseSettings();
		h.Model.OpenSettings();
		Assert.Equal("tabs", h.Model.State().SettingsCategory);
	}

	[Fact]
	public void An_unknown_category_is_refused()
	{
		var h = new Harness();
		Assert.Throws<UnknownSettingsCategoryException>(() => h.Model.OpenSettings("nope"));
		Assert.False(h.Model.SettingsOpen);
	}

	[Fact]
	public void Closing_settings_hides_the_surface_and_returns_focus_to_the_page()
	{
		var h = new Harness();
		h.Model.OpenSettings();
		h.Events.Clear();
		h.Model.CloseSettings();
		Assert.Null(h.Model.Region);
		Assert.Null(h.Regions[^1]);
		Assert.False(h.States[^1].SettingsOpen);
		Assert.Equal(["focus page"], h.Events);
	}

	[Fact]
	public void Closing_settings_that_are_not_open_does_nothing()
	{
		var h = new Harness();
		h.Model.CloseSettings();
		Assert.Empty(h.States);
		Assert.Empty(h.Events);
	}

	[Fact]
	public void A_gated_start_covers_the_window_and_asks_for_focus()
	{
		var h = new Harness(gated: true);
		Assert.True(h.Model.Gated);
		Assert.Equal(new Rect(0, 0, 1280, 780), h.Model.Region);
		Assert.Equal(["focus surface"], h.Events);
	}

	[Fact]
	public void Signing_in_lifts_the_gate_and_signing_out_brings_it_back()
	{
		var h = new Harness(gated: true);
		h.Events.Clear();
		h.Model.AccountChanged(signedIn: true);
		Assert.False(h.Model.Gated);
		Assert.Null(h.Model.Region);
		Assert.False(h.States[^1].Gated);
		Assert.Empty(h.Events);
		h.Model.AccountChanged(signedIn: false);
		Assert.True(h.States[^1].Gated);
		Assert.Equal(["focus surface"], h.Events);
	}

	[Fact]
	public void An_account_change_while_settings_are_open_keeps_focus_on_the_surface()
	{
		var h = new Harness();
		h.Model.OpenSettings();
		h.Events.Clear();
		h.Model.AccountChanged(signedIn: true);
		Assert.Equal(["focus surface"], h.Events);
	}

	[Fact]
	public void A_pinned_sidebar_never_reveals()
	{
		var h = new Harness(pinned: true);
		h.Cursor = new Point(2, 300);
		h.Ticks(10);
		Assert.False(h.Model.SidebarRevealed);
		Assert.Empty(h.States);
	}

	[Fact]
	public void An_unpinned_sidebar_reveals_at_the_edge_and_the_surface_hugs_it()
	{
		var h = new Harness(pinned: false);
		h.Cursor = new Point(2, 300);
		h.Ticks(3);
		Assert.True(h.Model.SidebarRevealed);
		Assert.Equal(new Rect(0, 0, 204, 780), h.Model.Region);
		Assert.True(h.States[^1].SidebarRevealed);
		h.Cursor = null;
		h.Ticks(1);
		Assert.False(h.Model.SidebarRevealed);
		Assert.Null(h.Model.Region);
		Assert.False(h.States[^1].SidebarRevealed);
	}

	[Fact]
	public void Unpinning_relays_out_the_page_and_peeks_at_the_sidebar()
	{
		var h = new Harness(pinned: true);
		h.Model.SetSidebarPinned(false);
		Assert.Equal(["relayout"], h.Events);
		Assert.True(h.Model.SidebarRevealed);
		Assert.Equal(0, h.Model.PinnedWidth);
		h.Ticks(40);
		Assert.False(h.Model.SidebarRevealed);
	}

	[Fact]
	public void Pinning_relays_out_the_page_and_puts_the_sidebar_away_at_once()
	{
		var h = new Harness(pinned: false);
		h.Cursor = new Point(2, 300);
		h.Ticks(3);
		h.Events.Clear();
		h.Model.SetSidebarPinned(true);
		Assert.Equal(["relayout"], h.Events);
		Assert.False(h.Model.SidebarRevealed);
		Assert.Equal(204, h.Model.PinnedWidth);
		Assert.Equal(0, h.Clock.PendingCount);
	}

	[Fact]
	public void Setting_the_pin_to_what_it_already_is_does_nothing()
	{
		var h = new Harness(pinned: true);
		h.Model.SetSidebarPinned(true);
		Assert.Empty(h.Events);
	}

	[Fact]
	public void Toggling_the_sidebar_asks_the_host_to_flip_the_setting()
	{
		var h = new Harness(pinned: true);
		h.Model.ToggleSidebar();
		Assert.Equal(["pin False"], h.Events);
	}

	[Fact]
	public void The_toolbox_is_not_there_yet()
	{
		var h = new Harness();
		h.Model.OpenToolbox();
		Assert.Equal([ShellModel.ToolboxNotice], h.Notices);
	}

	[Fact]
	public void Opening_a_panel_converts_the_screen_point_pushes_both_states_and_remembers_it()
	{
		var h = new Harness();
		var outcome = h.Model.OpenPanel("tool:Hashes", new Point(800, 450));
		Assert.Equal(OpenPanelOutcome.Opened, outcome);
		var view = Assert.Single(h.Model.PanelsState().Panels);
		// Screen (800, 450) is window (700, 400), as in the store's own centring rule.
		Assert.Equal(new Rect(440, 382, 520, 580), view.Frame);
		Assert.Equal("Hashes", view.Title);
		Assert.Equal(Json(h.Model.PanelsState()), Json(h.Panels[^1]));
		Assert.NotEmpty(h.States);
		Assert.Equal(["tool:Hashes"], Assert.Single(h.Recents));
		Assert.Equal(["tool:Hashes"], h.Model.RecentFeatures);
	}

	[Fact]
	public void An_open_panel_makes_the_surface_hug_it_plus_its_shadow()
	{
		var h = new Harness();
		h.Model.OpenPanel("tool:Hashes", new Point(800, 450));
		Assert.Equal(new Rect(408, 350, 584, 430), h.Model.Region);
		Assert.Equal(new Point(408, 350), h.States[^1].Origin);
	}

	[Fact]
	public void Opening_the_settings_section_in_a_panel_is_refused_as_not_tearable()
	{
		var h = new Harness();
		Assert.Throws<PanelNotTearableException>(() => h.Model.OpenPanelCentred("section:Settings"));
		Assert.Empty(h.Recents);
	}

	[Fact]
	public void Recents_are_most_recent_first_and_capped_at_three()
	{
		var h = new Harness();
		foreach (var id in new[] { "tool:Hashes", "tool:JWT Decoder", "tool:Base64", "tool:HMAC" })
		{
			h.Model.OpenPanelCentred(id);
			h.Model.ClosePanel(h.Model.PanelsState().Panels[^1].Id);
		}
		Assert.Equal(["tool:HMAC", "tool:Base64", "tool:JWT Decoder"], h.Model.RecentFeatures);
	}

	[Fact]
	public void The_host_can_hand_the_model_a_stored_recents_list()
	{
		var h = new Harness(recents: ["tool:Hashes"]);
		h.Model.SetRecentFeatures(["tool:Hashes", "tool:UUID"]);
		h.Model.OpenPanelCentred("tool:Base64");
		Assert.Equal(["tool:Base64", "tool:Hashes", "tool:UUID"], h.Model.RecentFeatures);
	}

	[Fact]
	public void A_fourth_panel_raises_a_notice_and_pushes_nothing()
	{
		var h = new Harness();
		foreach (var id in new[] { "tool:Hashes", "tool:JWT Decoder", "tool:Base64" }) h.Model.OpenPanelCentred(id);
		h.States.Clear();
		h.Panels.Clear();
		h.Recents.Clear();
		var outcome = h.Model.OpenPanelCentred("tool:HMAC");
		Assert.Equal(OpenPanelOutcome.Full, outcome);
		Assert.Equal([ShellModel.FullNotice], h.Notices);
		Assert.Empty(h.States);
		Assert.Empty(h.Panels);
		Assert.Empty(h.Recents);
	}

	[Fact]
	public void An_id_that_names_no_content_is_refused()
	{
		var h = new Harness();
		Assert.Throws<UnknownPanelContentException>(() => h.Model.OpenPanel("tool:Nope", new Point(0, 0)));
		Assert.Throws<UnknownPanelContentException>(() => h.Model.OpenPanelCentred("Hashes"));
	}

	[Fact]
	public void Focusing_a_panel_pushes_panels_only()
	{
		var h = new Harness();
		h.Model.OpenPanelCentred("tool:Hashes");
		h.Model.OpenPanelCentred("tool:JWT Decoder");
		var first = h.Model.PanelsState().Panels[0].Id;
		h.States.Clear();
		h.Panels.Clear();
		h.Model.FocusPanel(first);
		Assert.Empty(h.States);
		Assert.Equal(first, Assert.Single(h.Panels).FrontId);
	}

	[Fact]
	public void Dragging_covers_the_page_and_releasing_shrinks_the_surface_again()
	{
		var h = new Harness();
		h.Model.OpenPanelCentred("tool:Hashes");
		var id = h.Model.PanelsState().Panels[0].Id;
		h.Model.BeginPanelDrag(id);
		Assert.True(h.Model.PanelsState().Dragging);
		Assert.Equal(h.Page, h.Model.Region);
		h.Model.EndPanelDrag(id, new Point(300, 120));
		Assert.False(h.Model.PanelsState().Dragging);
		Assert.Equal(new Rect(300, 120, 520, 580), h.Model.PanelsState().Panels[0].Frame);
		Assert.NotEqual(h.Page, h.Model.Region);
	}

	[Fact]
	public void Minimizing_restoring_and_closing_push_both_states()
	{
		var h = new Harness();
		h.Model.OpenPanelCentred("tool:Hashes");
		var id = h.Model.PanelsState().Panels[0].Id;
		h.Model.MinimizePanel(id);
		Assert.True(h.Model.PanelsState().Panels[0].Minimized);
		Assert.Null(h.Model.Region);
		Assert.Equal(OpenPanelOutcome.Restored, h.Model.RestorePanel(id));
		Assert.NotNull(h.Model.Region);
		h.Model.ClosePanel(id);
		Assert.Empty(h.Model.PanelsState().Panels);
		Assert.Null(h.Model.Region);
		Assert.Throws<UnknownPanelException>(() => h.Model.ClosePanel(id));
	}

	[Fact]
	public void Restoring_into_a_full_window_raises_the_notice()
	{
		var h = new Harness();
		foreach (var id in new[] { "tool:Hashes", "tool:JWT Decoder", "tool:Base64" }) h.Model.OpenPanelCentred(id);
		var first = h.Model.PanelsState().Panels[0].Id;
		h.Model.MinimizePanel(first);
		h.Model.OpenPanelCentred("tool:HMAC");
		Assert.Equal(OpenPanelOutcome.Full, h.Model.RestorePanel(first));
		Assert.Equal([ShellModel.FullNotice], h.Notices);
	}

	[Fact]
	public void A_layout_change_keeps_panels_within_reach_and_pushes_both()
	{
		var h = new Harness();
		h.Model.OpenPanelCentred("tool:Hashes");
		h.States.Clear();
		h.Panels.Clear();
		h.Page = new Rect(204, 80, 300, 300);
		h.Model.LayoutChanged();
		var frame = h.Model.PanelsState().Panels[0].Frame;
		Assert.True(frame.X <= 204 + 300 - 60);
		Assert.True(frame.Y <= 80 + 300 - 40);
		Assert.Single(h.Panels);
		Assert.Single(h.States);
	}

	[Fact]
	public void Menus_offer_open_and_remove_for_recents_and_restore_and_close_for_minimized()
	{
		var h = new Harness(recents: ["tool:Hashes"]);
		var recent = h.Model.MenuItems("recent", "tool:Hashes");
		Assert.Equal(["Open in Window", "", "Remove from Sidebar"], recent.Select(i => i.Label));
		Assert.Null(recent[1].Invoke);
		recent[0].Invoke!();
		Assert.Single(h.Model.PanelsState().Panels);
		recent[2].Invoke!();
		Assert.Empty(h.Model.RecentFeatures);

		var id = h.Model.PanelsState().Panels[0].Id;
		h.Model.MinimizePanel(id);
		var minimized = h.Model.MenuItems("minimized", id);
		Assert.Equal(["Restore", "Close"], minimized.Select(i => i.Label));
		minimized[0].Invoke!();
		Assert.False(h.Model.PanelsState().Panels[0].Minimized);
		minimized[1].Invoke!();
		Assert.Empty(h.Model.PanelsState().Panels);
	}

	[Fact]
	public void An_unknown_menu_kind_is_refused()
	{
		Assert.Throws<UnknownShellMenuKindException>(() => new Harness().Model.MenuItems("nope", "x"));
	}
}
