using System.Text.Json.Serialization;

namespace Zing.Core;

/// <summary>One step of a tab's back/forward history as Zing keeps it: where it was, and what it was called.</summary>
public sealed record TabHistoryEntry(string Url, string Title);

/// <summary>A tab's back/forward history, oldest first, and the entry the tab is on.</summary>
public sealed record TabHistory(IReadOnlyList<TabHistoryEntry> Entries, int Index)
{
	/// <summary>As many as the engine keeps for a page.</summary>
	public const int MaxEntries = 50;

	[JsonIgnore]
	public bool IsValid => Index >= 0 && Index < Entries.Count;

	[JsonIgnore]
	public TabHistoryEntry Current => Entries[Index];

	/// <summary>The engine's history for a page, with the entry it is on as the tab has it now (the snapshot may lag a title).</summary>
	public static TabHistory Of(PageHistory page, TabHistoryEntry current)
	{
		if (page.Entries.Count == 0 || page.ActiveIndex < 0 || page.ActiveIndex >= page.Entries.Count) return new([current], 0);
		var entries = page.Entries.Select(e => new TabHistoryEntry(e.Url, e.Title)).ToList();
		entries[page.ActiveIndex] = current;
		return new(entries, page.ActiveIndex);
	}

	/// <summary>
	/// The entries <paramref name="keep"/> keeps, in the form <paramref name="map"/> gives them, at most <see cref="MaxEntries"/>
	/// nearest the current one. Null when the current entry isn't kept or nothing is left to go back or forward to.
	/// </summary>
	public TabHistory? Kept(Func<string, bool> keep, Func<string, string> map)
	{
		if (!IsValid || !keep(Current.Url)) return null;
		var before = Entries.Take(Index).Where(e => keep(e.Url)).ToList();
		var all = before.Append(Current).Concat(Entries.Skip(Index + 1).Where(e => keep(e.Url))).ToList();
		if (all.Count == 1) return null;
		var start = Math.Clamp(before.Count - MaxEntries / 2, 0, Math.Max(0, all.Count - MaxEntries));
		return new([.. all.Skip(start).Take(MaxEntries).Select(e => e with { Url = map(e.Url) })], before.Count - start);
	}
}

/// <summary>
/// The part of a tab's history its page doesn't have. A slept or restored tab's page starts with one entry, so the entries
/// before and after it stay here. Back and Forward walk them by replacing the page's first entry, so the engine's own list
/// never gains steps it would then walk itself, and the engine's Back and Forward take over past that entry. A new document
/// the page goes on to drops what was ahead, as in any browser; what was behind stays reachable from the page's first entry.
/// </summary>
public sealed class CarriedHistory
{
	private readonly List<TabHistoryEntry> _before;
	private readonly List<TabHistoryEntry> _after;

	public CarriedHistory(TabHistory history)
	{
		if (!history.IsValid) throw new ArgumentException("The history's index is not one of its entries", nameof(history));
		_before = [.. history.Entries.Take(history.Index)];
		_after = [.. history.Entries.Skip(history.Index + 1)];
	}

	public bool IsEmpty => _before.Count == 0 && _after.Count == 0;

	public bool CanGoBack(bool pageCanGoBack) => pageCanGoBack || _before.Count > 0;

	/// <summary>What is ahead here is ahead of the page's first entry, so it counts only while the page is on that entry.</summary>
	public bool CanGoForward(bool pageCanGoBack, bool pageCanGoForward) => pageCanGoForward || (!pageCanGoBack && _after.Count > 0);

	/// <summary>The entry to put in place of <paramref name="current"/>, or null when the page's own Back applies.</summary>
	public TabHistoryEntry? Back(bool pageCanGoBack, TabHistoryEntry current)
	{
		if (pageCanGoBack || _before.Count == 0) return null;
		var target = _before[^1];
		_before.RemoveAt(_before.Count - 1);
		_after.Insert(0, current);
		return target;
	}

	/// <summary>The entry to put in place of <paramref name="current"/>, or null when the page's own Forward applies.</summary>
	public TabHistoryEntry? Forward(bool pageCanGoBack, TabHistoryEntry current)
	{
		if (pageCanGoBack || _after.Count == 0) return null;
		var target = _after[0];
		_after.RemoveAt(0);
		_before.Add(current);
		return target;
	}

	/// <summary>
	/// The page's history changed. Past its first entry, it went on to a new document, which drops what was ahead: the
	/// engine's Forward from there is the page's own.
	/// </summary>
	public void PageMoved(bool pageCanGoBack)
	{
		if (pageCanGoBack) _after.Clear();
	}

	/// <summary>A tab without a page was sent somewhere new: where it was goes behind, and what was ahead goes.</summary>
	public void Left(TabHistoryEntry current)
	{
		_before.Add(current);
		_after.Clear();
	}

	/// <summary>The whole history, given the page's (empty for a tab with no page): what is here around the page's first entry.</summary>
	public TabHistory Around(PageHistory page, TabHistoryEntry current)
	{
		var own = TabHistory.Of(page, current);
		var entries = _before.Append(own.Entries[0]).Concat(_after).Concat(own.Entries.Skip(1)).ToList();
		var index = _before.Count + (own.Index == 0 ? 0 : _after.Count + own.Index);
		return new(entries, index);
	}
}
