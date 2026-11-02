namespace Zing.Core;

public sealed class SplitException(string detail) : Exception(detail);

/// <summary>
/// Split view (src/shared/layout/split.ts): 2–4 tabs side by side in one window. A split is a layout over tab ids,
/// not a special kind of tab, so sleeping, restore and ordering keep working on plain tabs.
/// </summary>
/// <param name="Orientation">"row" puts the panes side by side, "column" one above the other.</param>
/// <param name="Ratios">One per pane, summing to 1.</param>
public sealed record SplitLayout(string Id, string Orientation, IReadOnlyList<string> TabIds, IReadOnlyList<double> Ratios);

public static class Split
{
	public const int MinPanes = 2;
	public const int MaxPanes = 4;
	public const int DividerPx = 4;
	public const int MinPanePx = 240;

	public static SplitLayout Create(string id, IReadOnlyList<string> tabIds, string orientation)
	{
		AssertPaneCount(tabIds.Count);
		if (tabIds.Distinct().Count() != tabIds.Count) throw new SplitException("A tab can only appear once in a split");
		if (orientation is not ("row" or "column")) throw new SplitException($"Unknown split orientation {orientation}");
		return new SplitLayout(id, orientation, [.. tabIds], [.. tabIds.Select(_ => 1.0 / tabIds.Count)]);
	}

	/// <summary>The new pane takes an equal share; the others shrink in proportion.</summary>
	public static SplitLayout AddPane(SplitLayout layout, string tabId, int at)
	{
		if (layout.TabIds.Contains(tabId)) throw new SplitException($"Tab {tabId} is already in this split");
		AssertPaneCount(layout.TabIds.Count + 1);
		var share = 1.0 / (layout.TabIds.Count + 1);
		var ratios = layout.Ratios.Select(r => r * (1 - share)).ToList();
		var place = Math.Clamp(at, 0, layout.TabIds.Count);
		ratios.Insert(place, share);
		var tabIds = layout.TabIds.ToList();
		tabIds.Insert(place, tabId);
		return layout with { TabIds = tabIds, Ratios = ratios };
	}

	/// <summary>Null when only one tab would be left: a split of one is just a tab.</summary>
	public static SplitLayout? RemovePane(SplitLayout layout, string tabId)
	{
		var index = layout.TabIds.ToList().IndexOf(tabId);
		if (index < 0) throw new SplitException($"Tab {tabId} is not in this split");
		if (layout.TabIds.Count - 1 < MinPanes) return null;
		var kept = layout.Ratios.Where((_, i) => i != index).ToList();
		var total = kept.Sum();
		return layout with { TabIds = [.. layout.TabIds.Where(id => id != tabId)], Ratios = [.. kept.Select(r => r / total)] };
	}

	/// <summary>
	/// Whole pixels, as Krackn's backingAlignedRect: a page laid out across half a pixel gets its whole composited
	/// layer resampled. The last pane takes up the rounding, so the panes always fill the container exactly.
	/// </summary>
	public static IReadOnlyList<Rect> PaneRects(Rect container, SplitLayout layout, int divider = DividerPx)
	{
		var row = layout.Orientation == "row";
		var origin = (int)Math.Round(row ? container.X : container.Y, MidpointRounding.AwayFromZero);
		var end = (int)Math.Round(row ? container.X + container.Width : container.Y + container.Height, MidpointRounding.AwayFromZero);
		var crossAt = (int)Math.Round(row ? container.Y : container.X, MidpointRounding.AwayFromZero);
		var crossSize = (int)Math.Round(row ? container.Height : container.Width, MidpointRounding.AwayFromZero);
		var usable = end - origin - divider * (layout.TabIds.Count - 1);
		var cumulative = 0.0;
		var rects = new List<Rect>();
		for (var i = 0; i < layout.Ratios.Count; i++)
		{
			var start = origin + (int)Math.Round(cumulative * usable, MidpointRounding.AwayFromZero) + divider * i;
			cumulative += layout.Ratios[i];
			var stop = i == layout.Ratios.Count - 1 ? end : origin + (int)Math.Round(cumulative * usable, MidpointRounding.AwayFromZero) + divider * i;
			var span = stop - start;
			rects.Add(row ? new Rect(start, crossAt, span, crossSize) : new Rect(crossAt, start, crossSize, span));
		}
		return rects;
	}

	/// <summary>
	/// New pane sizes from the chrome (a divider dragged): one per pane, each above zero, summing to 1, and no pane
	/// below the minimum size in a container of <paramref name="containerLength"/> (a narrow window may have less).
	/// </summary>
	public static SplitLayout WithRatios(SplitLayout layout, IReadOnlyList<double> ratios, double containerLength)
	{
		if (ratios.Count != layout.TabIds.Count) throw new SplitException($"A {layout.TabIds.Count}-pane split needs {layout.TabIds.Count} sizes, not {ratios.Count}");
		if (ratios.Any(r => !double.IsFinite(r) || r <= 0) || Math.Abs(ratios.Sum() - 1) > 1e-6) throw new SplitException("Pane sizes must be positive and add up to 1");
		var usable = containerLength - DividerPx * (layout.TabIds.Count - 1);
		var floor = Math.Min(MinPanePx, usable / layout.TabIds.Count) - 1;
		if (ratios.Any(r => r * usable < floor)) throw new SplitException($"No pane may be narrower than {MinPanePx} px");
		return layout with { Ratios = [.. ratios] };
	}

	/// <summary>A tab dropped onto a page's edge picks the orientation and its side (splitFromDrop).</summary>
	public static SplitLayout FromDrop(string id, string targetTabId, string droppedTabId, string edge)
	{
		var orientation = edge switch
		{
			"left" or "right" => "row",
			"top" or "bottom" => "column",
			_ => throw new SplitException($"Unknown drop edge {edge}"),
		};
		var first = edge is "left" or "top";
		return Create(id, first ? [droppedTabId, targetTabId] : [targetTabId, droppedTabId], orientation);
	}

	private static void AssertPaneCount(int count)
	{
		if (count is < MinPanes or > MaxPanes) throw new SplitException($"A split holds {MinPanes}–{MaxPanes} tabs, not {count}");
	}
}

/// <summary>One window's splits. A tab is in at most one.</summary>
public sealed class SplitSet
{
	private readonly List<SplitLayout> _splits = [];

	public IReadOnlyList<SplitLayout> All => _splits;

	public SplitLayout? Of(string tabId) => _splits.FirstOrDefault(s => s.TabIds.Contains(tabId));

	public SplitLayout Get(string splitId) => _splits.FirstOrDefault(s => s.Id == splitId) ?? throw new SplitException($"No split {splitId}");

	/// <summary>The tabs shown together with this one: its split's, in pane order, or just itself.</summary>
	public IReadOnlyList<string> GroupOf(string tabId) => Of(tabId)?.TabIds ?? [tabId];

	public void Put(SplitLayout layout)
	{
		var at = _splits.FindIndex(s => s.Id == layout.Id);
		if (layout.TabIds.Any(t => Of(t) is { } other && other.Id != layout.Id)) throw new SplitException("A tab can only be in one split");
		if (at >= 0) _splits[at] = layout;
		else _splits.Add(layout);
	}

	/// <summary>Takes a tab out of its split; a split left with one tab dissolves. False when it wasn't in one.</summary>
	public bool Remove(string tabId)
	{
		if (Of(tabId) is not { } split) return false;
		var at = _splits.IndexOf(split);
		if (Split.RemovePane(split, tabId) is { } rest) _splits[at] = rest;
		else _splits.RemoveAt(at);
		return true;
	}
}
