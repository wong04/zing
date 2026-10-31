namespace Zing.Core;

/// <summary>A point in DIPs. Serialises as {x, y} like the TypeScript <c>Point</c>.</summary>
public readonly record struct Point(double X, double Y);

/// <summary>
/// Where floating panels go so they never sit on top of each other (src/shared/shell/panelLayout.ts).
/// Pure geometry: the panel being placed always wins its spot, and everything it lands on moves
/// the shortest distance that clears it while staying inside the window.
/// </summary>
public static class PanelLayout
{
	/// <summary>Breathing room left between two panels after one is pushed.</summary>
	public const double Gap = 12;

	/// <summary>Panels keep this far inside the window edge when they are pushed.</summary>
	public const double Inset = 8;

	/// <summary>Depth of the search: how many of each panel's nearest free spots are tried.</summary>
	private const int SpotsPerPanel = 10;

	/// <summary>On a drop, moving the dropped panel costs this much more than moving the others.</summary>
	private const double FixedMoveWeight = 3;

	private sealed record Arrangement(double Cost, Rect[] Frames);

	public static double MaxX(Rect r) => r.X + r.Width;

	public static double MaxY(Rect r) => r.Y + r.Height;

	public static Rect InsetBy(Rect r, double dx, double dy) => new(r.X + dx, r.Y + dy, r.Width - dx * 2, r.Height - dy * 2);

	/// <summary>Rectangles that only share an edge do not intersect.</summary>
	public static bool Intersects(Rect a, Rect b) => a.X < MaxX(b) && b.X < MaxX(a) && a.Y < MaxY(b) && b.Y < MaxY(a);

	/// <summary>Edges are inclusive.</summary>
	public static bool Contains(Rect outer, Rect inner) =>
		inner.X >= outer.X && inner.Y >= outer.Y && MaxX(inner) <= MaxX(outer) && MaxY(inner) <= MaxY(outer);

	/// <summary>
	/// Resolves overlaps around <c>frames[fixedIndex]</c>, the panel being dragged or just opened. It
	/// keeps exactly the frame it was given; every other panel moves the least it can. A panel that
	/// cannot be cleared without leaving <paramref name="bounds"/> stays where it was.
	/// </summary>
	public static Rect[] Resolve(int fixedIndex, IReadOnlyList<Rect> frames, Rect bounds)
	{
		var fixedFrame = FrameAt(frames, fixedIndex);
		var others = OthersNearestFirst(fixedIndex, frames);
		var best = CheapestLayout(frames, others, bounds, fixedIndex, fixedFrame, 0, null);
		return best?.Frames ?? PlaceOneAtATime(fixedIndex, frames, others, bounds);
	}

	/// <summary>
	/// Like <see cref="Resolve"/>, for when the panel has been released rather than held mid-drag.
	/// If nothing fits around it where it is, it may slide too, at a heavy cost so it moves only
	/// when that is the only way.
	/// </summary>
	public static Rect[] ResolveAfterDrop(int fixedIndex, IReadOnlyList<Rect> frames, Rect bounds)
	{
		var home = FrameAt(frames, fixedIndex);
		var others = OthersNearestFirst(fixedIndex, frames);
		var best = CheapestLayout(frames, others, bounds, fixedIndex, home, 0, null);
		if (best is not null) return best.Frames;

		foreach (var spot in FreeSpots(home, [], bounds))
		{
			var moved = double.Hypot(spot.X - home.X, spot.Y - home.Y) * FixedMoveWeight;
			best = CheapestLayout(frames, others, bounds, fixedIndex, spot, moved, best);
		}
		return best?.Frames ?? PlaceOneAtATime(fixedIndex, frames, others, bounds);
	}

	/// <summary>
	/// Every place <paramref name="frame"/> could go without overlapping <paramref name="occupied"/>,
	/// nearest first. Its current position counts if it is already clear, even hanging off the window.
	/// </summary>
	public static List<Rect> FreeSpots(Rect frame, IReadOnlyList<Rect> occupied, Rect bounds)
	{
		var area = InsetBy(bounds, Inset, Inset);
		var xs = new List<double> { frame.X, area.X, MaxX(area) - frame.Width };
		var ys = new List<double> { frame.Y, area.Y, MaxY(area) - frame.Height };
		// Butting up against any occupied panel's edge, plus the window edges so a panel in a
		// corner can slide along the edge. Every combination is tried, which finds diagonal
		// escapes a one-axis push misses.
		foreach (var other in occupied)
		{
			xs.Add(other.X - frame.Width - Gap);
			xs.Add(MaxX(other) + Gap);
			ys.Add(other.Y - frame.Height - Gap);
			ys.Add(MaxY(other) + Gap);
		}

		var spots = new List<(Rect Spot, double Distance)>();
		foreach (var x in xs)
		{
			foreach (var y in ys)
			{
				var candidate = new Rect(x, y, frame.Width, frame.Height);
				if (occupied.Any(o => Intersects(o, candidate))) continue;
				var isHome = x == frame.X && y == frame.Y;
				if (!isHome && !Contains(area, candidate)) continue;
				spots.Add((candidate, double.Hypot(x - frame.X, y - frame.Y)));
			}
		}

		var seen = new HashSet<(double, double)>();
		var result = new List<Rect>();
		foreach (var (spot, _) in spots.OrderBy(s => s.Distance))
			if (seen.Add((spot.X, spot.Y))) result.Add(spot);
		return result;
	}

	/// <summary>
	/// A joint search, not one panel at a time: greedy placement fails when the first panel pushed
	/// takes the middle of the only free column. Branch and bound over each panel's few nearest
	/// free spots finds the layout that moves things least in total.
	/// </summary>
	private static Arrangement? CheapestLayout(
		IReadOnlyList<Rect> frames,
		IReadOnlyList<int> others,
		Rect bounds,
		int startIndex,
		Rect startFrame,
		double startCost,
		Arrangement? bestSoFar)
	{
		var best = bestSoFar;
		var current = frames.ToArray();
		current[startIndex] = startFrame;

		void Visit(int depth, List<Rect> placed, double cost)
		{
			if (best is not null && cost >= best.Cost) return;
			if (depth >= others.Count)
			{
				best = new Arrangement(cost, (Rect[])current.Clone());
				return;
			}
			var index = others[depth];
			var home = frames[index];
			foreach (var spot in FreeSpots(home, placed, bounds).Take(SpotsPerPanel))
			{
				current[index] = spot;
				Visit(depth + 1, [.. placed, spot], cost + double.Hypot(spot.X - home.X, spot.Y - home.Y));
			}
			current[index] = home;
		}

		Visit(0, [startFrame], startCost);
		return best;
	}

	/// <summary>No arrangement clears everything: do what can be done one at a time and leave the rest, rather than throwing anything off-screen.</summary>
	private static Rect[] PlaceOneAtATime(int fixedIndex, IReadOnlyList<Rect> frames, IReadOnlyList<int> others, Rect bounds)
	{
		var result = frames.ToArray();
		var placed = new List<Rect> { FrameAt(frames, fixedIndex) };
		foreach (var index in others)
		{
			var spots = FreeSpots(FrameAt(frames, index), placed, bounds);
			if (spots.Count > 0) result[index] = spots[0];
			placed.Add(result[index]);
		}
		return result;
	}

	/// <summary>Nearest first: the panel that was hit squarely gets first pick of free space, and the search meets good answers early.</summary>
	private static List<int> OthersNearestFirst(int fixedIndex, IReadOnlyList<Rect> frames)
	{
		var target = FrameAt(frames, fixedIndex);
		return Enumerable.Range(0, frames.Count)
			.Where(i => i != fixedIndex)
			.OrderBy(i => CentreDistance(frames[i], target))
			.ToList();
	}

	private static double CentreDistance(Rect a, Rect b) =>
		double.Hypot(a.X + a.Width / 2 - (b.X + b.Width / 2), a.Y + a.Height / 2 - (b.Y + b.Height / 2));

	private static Rect FrameAt(IReadOnlyList<Rect> frames, int index)
	{
		if (index < 0 || index >= frames.Count) throw new ArgumentOutOfRangeException(nameof(index), $"No panel at index {index} (have {frames.Count})");
		return frames[index];
	}
}

/// <summary>The sidebar's "recently used" list: most recent first, no duplicates, capped (src/shared/shell/recentList.ts).</summary>
public static class RecentList
{
	public const int Capacity = 3;

	public static List<string> Touching(string id, IEnumerable<string> list, int capacity = Capacity) =>
		[.. list.Where(item => item != id).Prepend(id).Take(capacity)];

	public static List<string> Removing(string id, IEnumerable<string> list) => [.. list.Where(item => item != id)];
}

/// <summary>The shapes the shell sends across the bridge (src/shared/shell/shellState.ts). They serialise with <see cref="ZingJson.Options"/>.</summary>
public static class ShellConstants
{
	public const int MaxVisiblePanels = 3;

	public static readonly IReadOnlyList<string> SettingsCategories =
		["general", "browsing", "newTab", "tabs", "files", "privacy", "security", "advanced", "account", "about"];

	public const string DefaultCategory = "general";

	public static bool IsSettingsCategory(string? value) => value is not null && SettingsCategories.Contains(value);
}

/// <summary>The surface view's state. <c>Origin</c> is where that view sits in window content coordinates.</summary>
public sealed record ShellState(
	bool SettingsOpen,
	string SettingsCategory,
	bool SidebarRevealed,
	bool Gated,
	Point Origin,
	string AppVersion);

public sealed record ToolPanelView(string Id, string Content, string Title, Rect Frame, bool Minimized);

public sealed record ToolPanelsState(IReadOnlyList<ToolPanelView> Panels, string? FrontId, bool Dragging)
{
	public static readonly ToolPanelsState Empty = new([], null, false);
}

/// <summary>Status is opened, focused, restored or full (three are already showing).</summary>
public sealed record OpenPanelOutcome(string Status)
{
	public static readonly OpenPanelOutcome Opened = new("opened");
	public static readonly OpenPanelOutcome Focused = new("focused");
	public static readonly OpenPanelOutcome Restored = new("restored");
	public static readonly OpenPanelOutcome Full = new("full");
}
