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
	string? Icon = null);

/// <summary>src/shared/ipc.ts TabsState.</summary>
public sealed record TabsState(IReadOnlyList<TabView> Tabs, string? ActiveId, string Organization, bool Dropdowns, string? OpenStack);

/// <summary>Per-tab state that only exists while a page is attached.</summary>
public sealed record PageState(bool Loading, bool CanGoBack, bool CanGoForward);

public sealed class UnknownTabException(string id) : Exception($"No tab {id}");

/// <summary>
/// The window's tab records in strip order. W1 keeps to what one page needs;
/// pinning, moving, ordering and sleeping come over from tab-service.ts in W2.
/// </summary>
public sealed class TabStore(Func<long> now)
{
	public const string NewTabTitle = "New Tab";

	private readonly List<TabRecord> _tabs = [];
	private readonly Dictionary<string, PageState> _pages = new(StringComparer.Ordinal);

	public string? ActiveId { get; private set; }

	public IReadOnlyList<TabRecord> Records => _tabs;

	public TabRecord Open(string url)
	{
		var time = now();
		var record = new TabRecord(Guid.NewGuid().ToString(), url, NewTabTitle, false, time, time, time);
		_tabs.Add(record);
		ActiveId = record.Id;
		return record;
	}

	public TabRecord Get(string id) => _tabs.Find(t => t.Id == id) ?? throw new UnknownTabException(id);

	public void Update(string id, string? url = null, string? title = null)
	{
		var index = IndexOf(id);
		var current = _tabs[index];
		_tabs[index] = current with
		{
			Url = url ?? current.Url,
			Title = title is null ? current.Title : string.IsNullOrWhiteSpace(title) ? NewTabTitle : title,
			UpdatedAt = now(),
		};
	}

	public void SetPage(string id, PageState state)
	{
		IndexOf(id);
		_pages[id] = state;
	}

	public void Activate(string id)
	{
		var index = IndexOf(id);
		_tabs[index] = _tabs[index] with { LastActiveAt = now() };
		ActiveId = id;
	}

	public TabsState Snapshot(string organization, bool dropdowns) =>
		new(_tabs.Select(View).ToList(), ActiveId, organization, dropdowns, null);

	private TabView View(TabRecord t)
	{
		var page = _pages.GetValueOrDefault(t.Id, new PageState(false, false, false));
		return new TabView(t.Id, t.Url, t.Title, t.Pinned, t.CreatedAt, t.LastActiveAt, t.UpdatedAt, page.Loading, page.CanGoBack, page.CanGoForward, t.FaviconUrl);
	}

	private int IndexOf(string id)
	{
		var index = _tabs.FindIndex(t => t.Id == id);
		if (index < 0) throw new UnknownTabException(id);
		return index;
	}
}
