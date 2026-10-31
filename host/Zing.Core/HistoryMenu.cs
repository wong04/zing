namespace Zing.Core;

/// <summary>One entry of a tab's session history; <see cref="Id"/> is what the engine navigates back to.</summary>
public sealed record HistoryEntry(int Id, string Url, string Title);

public sealed record PageHistory(IReadOnlyList<HistoryEntry> Entries, int ActiveIndex);

public sealed record HistoryMenuItem(int Index, string Label);

/// <summary>The menu behind the Back and Forward buttons (history-menu.ts).</summary>
public static class HistoryMenu
{
	public const int Limit = 15;
	private const int LabelLimit = 60;

	/// <summary>Nearest entry first: back walks toward the start, forward toward the end.</summary>
	public static List<HistoryMenuItem> Items(IReadOnlyList<HistoryEntry> entries, int activeIndex, string direction, int limit = Limit)
	{
		IEnumerable<int> indices = direction switch
		{
			"back" => Enumerable.Range(0, Math.Max(0, activeIndex)).Select(i => activeIndex - 1 - i),
			"forward" => Enumerable.Range(0, Math.Max(0, entries.Count - activeIndex - 1)).Select(i => activeIndex + 1 + i),
			_ => throw new ArgumentException($"Unknown history direction {direction}"),
		};
		return [.. indices.Take(limit).Where(i => i < entries.Count).Select(i => new HistoryMenuItem(i, LabelOf(entries[i])))];
	}

	private static string LabelOf(HistoryEntry entry)
	{
		var text = entry.Title.Trim() is { Length: > 0 } title ? title : entry.Url;
		return text.Length > LabelLimit ? $"{text[..(LabelLimit - 1)]}…" : text;
	}
}
