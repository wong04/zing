using System.Text.RegularExpressions;

namespace Zing.Core;

/// <summary>A tab as data (src/shared/model/tab.ts TabRecord); it exists with or without a live page.</summary>
public sealed record TabRecord(
	string Id,
	string Url,
	string Title,
	bool Pinned,
	long CreatedAt,
	long LastActiveAt,
	long UpdatedAt,
	string? FaviconUrl = null);

/// <summary>The fields of a tab that page events change (TabContentPatch); null leaves a field as it is.</summary>
public sealed record TabContentPatch(string? Url = null, string? Title = null, string? FaviconUrl = null);

/// <summary>What the chrome draws for a tab (src/shared/ipc.ts TabView).</summary>
public sealed record TabView(
	string Id,
	string Url,
	string Title,
	bool Pinned,
	long CreatedAt,
	long LastActiveAt,
	long UpdatedAt,
	bool Loading,
	bool CanGoBack,
	bool CanGoForward,
	string? FaviconUrl = null,
	string? Icon = null,
	bool? Sleeping = null);

/// <summary>src/shared/ipc.ts TabsState.</summary>
public sealed record TabsState(IReadOnlyList<TabView> Tabs, string? ActiveId, string Organization, bool Dropdowns, string? OpenStack);

/// <summary>Per-tab state that only exists while a page is attached.</summary>
public sealed record PageState(bool Loading, bool CanGoBack, bool CanGoForward);

public sealed record ClosedTab(string Url, string Title, bool Pinned);

/// <summary>NextActiveId is set when the closed tab was active and another tab took its place.</summary>
public sealed record CloseResult(TabRecord Closed, string? NextActiveId = null);

public sealed class UnknownTabException(string tabId) : Exception($"No tab {tabId}")
{
	public string TabId { get; } = tabId;
}

/// <summary>The arithmetic behind the tab shortcuts (src/shared/library/tabNavigation.ts).</summary>
public static class TabNavigation
{
	public static int? NextTab(int after, int count) => count <= 0 ? null : (after + 1) % count;

	public static int? PreviousTab(int before, int count) => count <= 0 ? null : (before - 1 + count) % count;

	/// <summary>Ctrl+1-8 pick that tab if it exists; Ctrl+9 always means the last one.</summary>
	public static int? TabForShortcut(int number, int count)
	{
		if (count <= 0 || number < 1 || number > 9) return null;
		if (number == 9) return count - 1;
		return number <= count ? number - 1 : null;
	}
}

/// <summary>Recently closed tabs, newest on top. Memory only: a list of closed pages on disk would be history.</summary>
public sealed class ClosedStack<T>(int capacity = 25)
{
	private readonly List<T> _items = [];

	public IReadOnlyList<T> Items => _items;

	public bool IsEmpty => _items.Count == 0;

	public void Push(T item)
	{
		_items.Add(item);
		if (_items.Count > capacity) _items.RemoveRange(0, _items.Count - capacity);
	}

	public bool TryPop(out T item)
	{
		if (_items.Count == 0)
		{
			item = default!;
			return false;
		}
		item = _items[^1];
		_items.RemoveAt(_items.Count - 1);
		return true;
	}
}

/// <summary>
/// The ordered tab list of one window, as pure data (src/shared/model/tab-store.ts).
/// Pinned tabs always form a run at the front.
/// </summary>
public sealed partial class TabStore(Func<long> now, Func<string>? newId = null)
{
	public const string NewTabTitle = "New Tab";
	public const string NewTabUrl = "about:blank";

	private readonly List<TabRecord> _tabs = [];
	private readonly ClosedStack<ClosedTab> _closed = new();
	private readonly Func<string> _newId = newId ?? (() => Guid.NewGuid().ToString());

	public string? ActiveId { get; private set; }

	public IReadOnlyList<TabRecord> Records => _tabs;

	public TabRecord? Active => ActiveId is null ? null : Get(ActiveId);

	public bool CanReopen => !_closed.IsEmpty;

	public int PinnedCount => _tabs.Count(t => t.Pinned);

	public bool Has(string id) => _tabs.Exists(t => t.Id == id);

	public TabRecord Get(string id) => _tabs[IndexOf(id)];

	public int IndexOf(string id)
	{
		var index = _tabs.FindIndex(t => t.Id == id);
		return index < 0 ? throw new UnknownTabException(id) : index;
	}

	/// <summary>Appends a new tab without activating it.</summary>
	public TabRecord Add(string url)
	{
		var record = Create(url);
		_tabs.Add(record);
		return record;
	}

	/// <summary>Inserts a new tab right after another, the way a popup lands beside its opener.</summary>
	public TabRecord AddAfter(string anchor, string url)
	{
		var record = Create(url);
		var at = Math.Max(IndexOf(anchor) + 1, PinnedCount);
		_tabs.Insert(at, record);
		return record;
	}

	/// <summary>A tab brought back from a saved session: a record only, placed by its pinned state.</summary>
	public TabRecord AddRestored(string url, string title, bool pinned)
	{
		var record = Create(url) with { Title = title, Pinned = pinned };
		_tabs.Insert(pinned ? PinnedCount : _tabs.Count, record);
		return record;
	}

	/// <summary>Marks a tab as just used, e.g. the moment it stops being the active one.</summary>
	public void Touch(string id)
	{
		var index = IndexOf(id);
		_tabs[index] = _tabs[index] with { LastActiveAt = now() };
	}

	public TabRecord Activate(string id)
	{
		var index = IndexOf(id);
		var record = _tabs[index] with { LastActiveAt = now() };
		_tabs[index] = record;
		ActiveId = id;
		return record;
	}

	public TabRecord Update(string id, TabContentPatch patch)
	{
		var index = IndexOf(id);
		var current = _tabs[index];
		var changed = (patch.Url is not null && patch.Url != current.Url)
			|| (patch.Title is not null && patch.Title != current.Title)
			|| (patch.FaviconUrl is not null && patch.FaviconUrl != current.FaviconUrl);
		if (!changed) return current;
		var record = current with
		{
			Url = patch.Url ?? current.Url,
			Title = patch.Title ?? current.Title,
			FaviconUrl = patch.FaviconUrl ?? current.FaviconUrl,
			UpdatedAt = now(),
		};
		_tabs[index] = record;
		return record;
	}

	/// <summary>
	/// Removes a tab. If it was active, the tab that slides into its slot (or the
	/// new last tab) becomes active. Leaves an empty list empty.
	/// </summary>
	public CloseResult Close(string id)
	{
		var index = IndexOf(id);
		var closed = _tabs[index];
		_tabs.RemoveAt(index);
		if (IsRestorable(closed)) _closed.Push(new ClosedTab(closed.Url, closed.Title, closed.Pinned));
		if (ActiveId != id) return new CloseResult(closed);
		ActiveId = null;
		if (_tabs.Count == 0) return new CloseResult(closed);
		var next = _tabs[Math.Min(index, _tabs.Count - 1)];
		Activate(next.Id);
		return new CloseResult(closed, next.Id);
	}

	/// <summary>Brings back the most recently closed tab, unactivated. Null when there is none.</summary>
	public TabRecord? ReopenClosed()
	{
		if (!_closed.TryPop(out var closed)) return null;
		var record = Create(closed.Url) with { Title = closed.Title, Pinned = closed.Pinned };
		_tabs.Insert(record.Pinned ? PinnedCount : _tabs.Count, record);
		return record;
	}

	/// <summary>Moves a tab within its own section; the target is clamped so a drag never pins by position alone.</summary>
	public void Move(string id, int toIndex)
	{
		var from = IndexOf(id);
		var pinned = PinnedCount;
		var record = _tabs[from];
		var clamped = record.Pinned
			? Math.Min(Math.Max(toIndex, 0), pinned - 1)
			: Math.Min(Math.Max(toIndex, pinned), _tabs.Count - 1);
		if (clamped == from) return;
		_tabs.RemoveAt(from);
		_tabs.Insert(clamped, record);
	}

	/// <summary>A newly pinned tab joins the end of the pinned run.</summary>
	public void Pin(string id) => SetPinned(id, true);

	/// <summary>Drops to the start of the loose section.</summary>
	public void Unpin(string id) => SetPinned(id, false);

	public string? NextId() => IdAt(TabNavigation.NextTab(ActiveIndex(), _tabs.Count));

	public string? PreviousId() => IdAt(TabNavigation.PreviousTab(ActiveIndex(), _tabs.Count));

	public string? IdForShortcut(int number) => IdAt(TabNavigation.TabForShortcut(number, _tabs.Count));

	/// <summary>The strip's state; page state is live, so the caller says where to find it.</summary>
	public TabsState Snapshot(string organization, bool dropdowns, Func<string, PageState?> pageState) =>
		new(_tabs.Select(t => View(t, pageState(t.Id))).ToList(), ActiveId, organization, dropdowns, null);

	private static TabView View(TabRecord t, PageState? page) =>
		new(t.Id, t.Url, t.Title, t.Pinned, t.CreatedAt, t.LastActiveAt, t.UpdatedAt, page?.Loading ?? false, page?.CanGoBack ?? false, page?.CanGoForward ?? false, t.FaviconUrl);

	private static bool IsRestorable(TabRecord record) => WebUrl().IsMatch(record.Url);

	[GeneratedRegex("^https?:", RegexOptions.IgnoreCase)]
	private static partial Regex WebUrl();

	private TabRecord Create(string url)
	{
		var time = now();
		return new TabRecord(_newId(), url, NewTabTitle, false, time, time, time);
	}

	private void SetPinned(string id, bool pinned)
	{
		var index = IndexOf(id);
		var current = _tabs[index];
		if (current.Pinned == pinned) return;
		_tabs.RemoveAt(index);
		_tabs.Insert(PinnedCount, current with { Pinned = pinned, UpdatedAt = now() });
	}

	private int ActiveIndex() => ActiveId is null ? -1 : IndexOf(ActiveId);

	private string? IdAt(int? index) => index is { } i && i < _tabs.Count ? _tabs[i].Id : null;
}
