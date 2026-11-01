using Zing.Core;

namespace Zing.Core.Tests;

/// <summary>The split model and layout (ported from src/shared/layout/layout.test.ts), and split view in the tab service.</summary>
public sealed class SplitTests
{
	private static readonly Rect Container = new(0, 40, 1208, 800);

	[Fact]
	public void A_split_needs_two_to_four_distinct_tabs_and_starts_with_equal_panes()
	{
		Assert.Throws<SplitException>(() => Split.Create("s", ["a"], "row"));
		Assert.Throws<SplitException>(() => Split.Create("s", ["a", "b", "c", "d", "e"], "row"));
		Assert.Throws<SplitException>(() => Split.Create("s", ["a", "a"], "row"));
		Assert.Equal([1 / 3.0, 1 / 3.0, 1 / 3.0], Split.Create("s", ["a", "b", "c"], "row").Ratios);
	}

	[Theory]
	[InlineData(2)]
	[InlineData(3)]
	[InlineData(4)]
	public void Panes_are_whole_pixels_and_fill_the_container_with_dividers_between(int count)
	{
		var rects = Split.PaneRects(Container, Split.Create("s", [.. "abcd".Take(count).Select(c => c.ToString())], "row"));
		Assert.All(rects, r => Assert.True(r.X == Math.Floor(r.X) && r.Width == Math.Floor(r.Width)));
		Assert.Equal(0, rects[0].X);
		Assert.Equal(1208, rects[^1].X + rects[^1].Width);
		for (var i = 1; i < rects.Count; i++) Assert.Equal(Split.DividerPx, rects[i].X - (rects[i - 1].X + rects[i - 1].Width));
		Assert.All(rects, r => Assert.Equal((40.0, 800.0), (r.Y, r.Height)));
	}

	[Fact]
	public void A_column_split_keeps_the_full_width()
	{
		var rects = Split.PaneRects(Container, Split.Create("s", ["a", "b"], "column"));
		Assert.All(rects, r => Assert.Equal((0.0, 1208.0), (r.X, r.Width)));
		Assert.Equal(840, rects[1].Y + rects[1].Height);
	}

	[Fact]
	public void Adding_takes_an_equal_share_and_removing_down_to_one_dissolves()
	{
		var two = Split.Create("s", ["a", "b"], "row");
		var three = Split.AddPane(two, "c", 3);
		Assert.Equal(["a", "b", "c"], three.TabIds);
		Assert.Equal(1.0, three.Ratios.Sum(), 9);
		Assert.Throws<SplitException>(() => Split.AddPane(Split.AddPane(three, "d", 4), "e", 5));
		Assert.Null(Split.RemovePane(two, "a"));
		Assert.Equal([0.5, 0.5], Split.RemovePane(three, "b")!.Ratios.Select(r => Math.Round(r, 9)));
	}

	[Fact]
	public void New_sizes_must_add_up_and_keep_every_pane_wide_enough()
	{
		var two = Split.Create("s", ["a", "b"], "row");
		Assert.Equal([0.3, 0.7], Split.WithRatios(two, [0.3, 0.7], 1208).Ratios);
		Assert.Throws<SplitException>(() => Split.WithRatios(two, [0.3, 0.6], 1208));
		Assert.Throws<SplitException>(() => Split.WithRatios(two, [0.05, 0.95], 1208));
		Assert.Throws<SplitException>(() => Split.WithRatios(two, [1.0], 1208));
	}

	[Fact]
	public void A_drop_on_an_edge_picks_the_side_and_the_orientation()
	{
		Assert.Equal("dropped target row", Describe(Split.FromDrop("s", "target", "dropped", "left")));
		Assert.Equal("target dropped column", Describe(Split.FromDrop("s", "target", "dropped", "bottom")));
	}

	private static string Describe(SplitLayout layout) => string.Join(' ', [.. layout.TabIds, layout.Orientation]);

	// ---- split view in the tab service ----

	private readonly FakePageHost _host = new();
	private readonly TabService _tabs;

	public SplitTests() => _tabs = new TabService(new TabStore(Deterministic.Clock(), Deterministic.Ids()), _host, "about:blank", () => { }, (_, _) => { });

	[Fact]
	public void Splitting_shows_both_pages_and_the_new_pane_has_the_focus()
	{
		var a = _tabs.Open("https://a.test/");
		var b = _tabs.Create("https://b.test/");
		_tabs.SplitWith(a.Id, b.Id);
		Assert.Equal(b.Id, _tabs.ActiveId);
		Assert.Equal([a.Id, b.Id], _host.ShownSplit!.TabIds);
		Assert.True(_host.PageFor(_tabs, a.Id).Shown && _host.PageFor(_tabs, b.Id).Shown);
	}

	[Fact]
	public void Moving_between_panes_hides_nothing_and_leaving_the_split_hides_every_pane()
	{
		var a = _tabs.Open("https://a.test/");
		var b = _tabs.Create("https://b.test/");
		var c = _tabs.Create("https://c.test/");
		_tabs.SplitWith(a.Id, b.Id);
		_tabs.Activate(a.Id);
		Assert.True(_host.PageFor(_tabs, b.Id).Shown);
		Assert.True(_tabs.IsShown(b.Id));
		_tabs.Activate(c.Id);
		Assert.False(_host.PageFor(_tabs, a.Id).Shown || _host.PageFor(_tabs, b.Id).Shown);
		Assert.False(_tabs.IsShown(a.Id));
	}

	[Fact]
	public void Closing_a_pane_of_two_dissolves_the_split_and_the_other_tab_shows_alone()
	{
		var a = _tabs.Open("https://a.test/");
		var b = _tabs.Create("https://b.test/");
		_tabs.SplitWith(a.Id, b.Id);
		_tabs.Close(a.Id);
		Assert.Empty(_tabs.Splits.All);
		Assert.Equal(b.Id, _tabs.ActiveId);
		Assert.Null(_host.ShownSplit);
		Assert.True(_host.PageFor(_tabs, b.Id).Shown);
	}

	[Fact]
	public void Taking_a_tab_out_of_a_split_leaves_the_rest_together()
	{
		var a = _tabs.Open("https://a.test/");
		var b = _tabs.Create("https://b.test/");
		var c = _tabs.Create("https://c.test/");
		_tabs.SplitWith(a.Id, b.Id);
		_tabs.SplitWith(a.Id, c.Id);
		_tabs.Unsplit(b.Id);
		Assert.Equal([a.Id, c.Id], _tabs.Splits.Of(a.Id)!.TabIds);
		Assert.False(_host.PageFor(_tabs, b.Id).Shown);
	}

	[Fact]
	public void A_drop_on_a_page_edge_splits_with_the_dropped_tab_on_that_side()
	{
		var a = _tabs.Open("https://a.test/");
		var b = _tabs.Create("https://b.test/");
		_tabs.SplitDrop(a.Id, b.Id, "left");
		Assert.Equal([b.Id, a.Id], _tabs.Splits.Of(a.Id)!.TabIds);
		Assert.Equal("row", _tabs.Splits.Of(a.Id)!.Orientation);
	}
}
