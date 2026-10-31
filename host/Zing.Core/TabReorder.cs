namespace Zing.Core;

public readonly record struct Rect(double X, double Y, double Width, double Height);

public enum Axis
{
	X,
	Y,
}

public sealed record TabBarLayoutResult(IReadOnlyList<double> LooseWidths, IReadOnlyList<bool> Compact);

/// <summary>src/shared/browser/tabReorder.ts: reorder geometry and tab widths.</summary>
public static class TabReorder
{
	public const double PinnedWidth = 34;
	/// <summary>Room for the close button at the left and the icon beside it.</summary>
	public const double IconWidth = 40;
	/// <summary>Below this a title is mostly ellipsis; drop it and show the icon alone.</summary>
	public const double MinimumTitled = 110;
	public const double DividerWidth = 9;

	private static double Min(Rect r, Axis axis) => axis == Axis.X ? r.X : r.Y;
	private static double Length(Rect r, Axis axis) => axis == Axis.X ? r.Width : r.Height;
	private static double Max(Rect r, Axis axis) => Min(r, axis) + Length(r, axis);
	private static double Mid(Rect r, Axis axis) => Min(r, axis) + Length(r, axis) / 2;

	/// <summary>Which slot a cursor at <paramref name="position"/> belongs in: a chip claims a slot once the cursor passes its midpoint.</summary>
	public static int TargetIndex(double position, IReadOnlyList<Rect> frames, Axis axis = Axis.X)
	{
		if (frames.Count == 0) return 0;
		for (var i = 0; i < frames.Count; i++)
			if (position < Mid(frames[i], axis)) return i;
		return frames.Count - 1;
	}

	/// <summary>Which slot the dragged chip belongs in, judged by where the chip is drawn: a neighbour gives way only once the chip's centre crosses the neighbour's centre.</summary>
	public static int TargetIndexDragging(int index, double visualCenter, IReadOnlyList<Rect> frames, Axis axis = Axis.X)
	{
		if (index < 0 || index >= frames.Count) return index;
		var target = index;
		while (target + 1 < frames.Count && visualCenter > Mid(frames[target + 1], axis)) target++;
		while (target - 1 >= 0 && visualCenter < Mid(frames[target - 1], axis)) target--;
		return target;
	}

	/// <summary>Where the dragged chip's centre will sit once moved from <paramref name="from"/> to <paramref name="to"/>, computed from the frames before the move.</summary>
	public static double CenterAfterMove(int from, int to, IReadOnlyList<Rect> frames, Axis axis = Axis.X)
	{
		if (from < 0 || from >= frames.Count) return 0;
		var source = frames[from];
		if (to < 0 || to >= frames.Count || to == from) return Mid(source, axis);
		var target = frames[to];
		var length = Length(source, axis);
		return to > from ? Max(target, axis) - length / 2 : Min(target, axis) + length / 2;
	}

	public static TabBarLayoutResult TabBarLayout(double total, int pinned, int loose, int? activeLoose, double spacing = 2)
	{
		if (loose <= 0) return new TabBarLayoutResult([], []);
		var available = total - pinned * (PinnedWidth + spacing);
		if (pinned > 0) available -= DividerWidth;
		var gaps = spacing * (loose - 1);
		var equal = (available - gaps) / loose;
		if (equal >= MinimumTitled) return new TabBarLayoutResult(Fill(loose, equal), Fill(loose, false));
		if (activeLoose is not { } active || loose <= 1) return new TabBarLayoutResult(Fill(loose, Math.Max(equal, IconWidth)), Fill(loose, true));
		var activeWidth = Math.Min(Math.Max(available * 0.25, 160), 240);
		var others = (available - activeWidth - gaps) / (loose - 1);
		if (others < IconWidth)
		{
			// Icons are at their smallest: the active tab gives back width so the row still fits.
			others = IconWidth;
			activeWidth = Math.Max(available - gaps - others * (loose - 1), IconWidth);
		}
		var widths = Fill(loose, others);
		widths[active] = activeWidth;
		var compact = Fill(loose, others < MinimumTitled);
		compact[active] = activeWidth < MinimumTitled;
		return new TabBarLayoutResult(widths, compact);
	}

	private static T[] Fill<T>(int count, T value) => Enumerable.Repeat(value, count).ToArray();
}
