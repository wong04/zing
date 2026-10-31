namespace Zing.Core;

/// <summary>A tab's live page. The WinUI host implements it over a WebView2; tests use a fake.</summary>
public interface IPage
{
	string Url { get; }
	string Title { get; }
	bool IsLoading { get; }
	bool CanGoBack { get; }
	bool CanGoForward { get; }
	void Navigate(string url);
	void GoBack();
	void GoForward();
	void Reload();
	void Stop();
	void Focus();
	void Close();
	/// <summary>Runs a script of Zing's own in the page, e.g. restyling the start page.</summary>
	void Run(string script);
	/// <summary>Finds in the page; results come back through <see cref="IPageEvents.FindResult"/>.</summary>
	void Find(string query, FindDirection direction, FindMode mode);
	/// <summary>Whether the page is playing sound; a playing tab never sleeps.</summary>
	bool IsAudible { get; }
	/// <summary>Runs a script and hands back its JSON result.</summary>
	Task<System.Text.Json.JsonElement> EvaluateAsync(string script);
	/// <summary>Runs a script once the current navigation has loaded, e.g. to put back a woken tab's scroll.</summary>
	void RunWhenLoaded(string script);
	/// <summary>The tab's text size in percent; the page keeps it across its own navigations.</summary>
	int TextSize { get; set; }
	void Print();
	/// <summary>A JPEG data URL of what the page shows now, or null when there is nothing to show.</summary>
	Task<string?> CaptureAsync();
	/// <summary>Swaps the document for its article; returns why not when the page has none. A new document leaves reader mode.</summary>
	Task<string?> ShowReaderAsync();
	bool InReader { get; }
	/// <summary>The tab's session history, oldest first.</summary>
	Task<PageHistory> HistoryAsync();
	void GoToHistoryEntry(int entryId);
	void StopFind();
}

/// <summary>What happens to a page's navigation.</summary>
public abstract record NavigationDecision
{
	public static readonly NavigationDecision Allow = new Allowed();
	public static readonly NavigationDecision Cancel = new Cancelled();

	public sealed record Allowed : NavigationDecision;
	public sealed record Cancelled : NavigationDecision;
	public sealed record Redirect(string Url) : NavigationDecision;
}

/// <summary>What a live page reports to its tab. The host calls these from the page's events.</summary>
public interface IPageEvents
{
	/// <summary>A main-frame navigation from <paramref name="from"/> is about to start.</summary>
	NavigationDecision Navigating(string from, string to);
	void Navigated(string url);
	void TitleChanged(string title);
	/// <summary>The page's icon as a data URL, kept in memory only; null when it has none.</summary>
	void IconChanged(string? dataUrl);
	/// <summary>Loading or history changed.</summary>
	void StateChanged();
	void LoadFailed(string description);
	/// <summary>How many matches the last find has, and which one (1-based) is current.</summary>
	void FindResult(int matches, int activeOrdinal);
	/// <summary>The page closed itself (window.close()).</summary>
	void Closed();
	/// <summary>The page opened a window; the returned page must be the one handed to the engine, so window.opener works.</summary>
	IPage Popup(string url, bool background);
}

/// <summary>Makes pages and puts them on screen. The window owns layout and stacking.</summary>
public interface IPageHost
{
	IPage Create(IPageEvents events);
	void Show(IPage page);
	void Hide(IPage page);
}

public sealed class ActiveTabSleepException(string tabId) : Exception($"Tab {tabId} is the active tab and cannot sleep");

public sealed class TabOrderException(string detail) : Exception($"Can't apply that tab order: {detail}");

/// <summary>
/// A window's tabs (tab-service.ts): records in a <see cref="TabStore"/>, and a
/// page attached to a record only when it is first activated. Every tab
/// operation goes through here.
/// </summary>
public sealed class TabService
{
	private readonly TabStore _store;
	private readonly IPageHost _host;
	private readonly string _blankUrl;
	private readonly Action _changed;
	private readonly Action<TabRecord, string> _loadFailed;
	private readonly Func<string, string, NavigationDecision?> _intercept;
	private readonly SiteDataKeeper? _siteData;

	/// <summary>A page reported find results; the window's find bar decides whether they are for it.</summary>
	public event Action<IPage, int, int>? FindResult;
	private readonly Dictionary<string, IPage> _pages = new(StringComparer.Ordinal);
	private readonly Dictionary<string, string> _openers = new(StringComparer.Ordinal);
	private readonly HashSet<string> _sleeping = new(StringComparer.Ordinal);
	private readonly Dictionary<string, string> _icons = new(StringComparer.Ordinal);

	/// <summary>A sleeping tab just got a fresh page, so its saved scroll can be put back.</summary>
	public event Action<string, IPage>? Woke;

	/// <summary>A tab's page is about to leave the screen: the last moment it can be pictured.</summary>
	public event Action<string, IPage>? Leaving;

	/// <summary>A tab closed for good.</summary>
	public event Action<string>? Closed;

	/// <param name="blankUrl">What a window that lost its last tab opens.</param>
	/// <param name="intercept">The window's say over a navigation (the start page's search and settings links); null leaves it to tracker stripping.</param>
	public TabService(TabStore store, IPageHost host, string blankUrl, Action changed, Action<TabRecord, string> loadFailed, Func<string, string, NavigationDecision?>? intercept = null, SiteDataKeeper? siteData = null)
	{
		_siteData = siteData;
		_intercept = intercept ?? ((_, _) => null);
		_store = store;
		_host = host;
		_blankUrl = blankUrl;
		_changed = changed;
		_loadFailed = loadFailed;
	}

	public string? ActiveId => _store.ActiveId;
	public bool CanReopen => _store.CanReopen;
	public IReadOnlyList<TabRecord> Records => _store.Records;

	public TabsState State(string organization, bool dropdowns)
	{
		var state = _store.Snapshot(organization, dropdowns, PageStateOf);
		return state with { Tabs = [.. state.Tabs.Select(t => t with { Sleeping = _sleeping.Contains(t.Id), Icon = _icons.GetValueOrDefault(t.Id) })] };
	}

	public bool IsSleeping(string id) => _sleeping.Contains(id);

	/// <summary>Drops the page but keeps the record, so the tab wakes at its URL when next activated.</summary>
	public void Sleep(string id)
	{
		_store.Get(id);
		if (id == _store.ActiveId) throw new ActiveTabSleepException(id);
		if (!_pages.ContainsKey(id)) return;
		DestroyPage(id);
		_sleeping.Add(id);
		_changed();
	}

	public bool HasPage(string id) => _pages.ContainsKey(id);

	public IPage? PageOf(string id) => _pages.GetValueOrDefault(id);

	public IPage? ActivePage => _store.ActiveId is { } id ? _pages.GetValueOrDefault(id) : null;

	public TabRecord Get(string id) => _store.Get(id);

	/// <summary>A record only; no page until it is activated.</summary>
	public TabRecord Create(string url)
	{
		var record = _store.Add(url);
		_changed();
		return record;
	}

	public TabRecord Open(string url)
	{
		var record = Create(url);
		Activate(record.Id);
		return record;
	}

	/// <summary>Brings back saved tabs as records; only the one that was in front gets a page.</summary>
	public void Restore(IReadOnlyList<SessionTab> tabs)
	{
		var records = tabs.Select(t => _store.AddRestored(t.Url, string.IsNullOrEmpty(t.Title) ? TabStore.NewTabTitle : t.Title, t.Pinned)).ToList();
		if (records.Count == 0) return;
		var front = tabs.ToList().FindIndex(t => t.Active);
		Activate(records[Math.Max(0, front)].Id);
	}

	public void Activate(string id)
	{
		var previous = _store.ActiveId;
		var record = _store.Activate(id);
		if (previous is not null && previous != id)
		{
			if (_pages.TryGetValue(previous, out var leaving))
			{
				Leaving?.Invoke(previous, leaving);
				_host.Hide(leaving);
			}
			if (_store.Has(previous)) _store.Touch(previous);
		}
		_host.Show(EnsurePage(record));
		_changed();
	}

	/// <summary>Closing the last tab leaves a fresh one, as in Krackn: a window is never tabless.</summary>
	public void Close(string id)
	{
		var result = _store.Close(id);
		DestroyPage(id);
		_sleeping.Remove(id);
		_icons.Remove(id);
		Closed?.Invoke(id);
		// After the page is gone, so nothing can write to the storage again as it is cleared.
		_siteData?.Release(id);
		_openers.Remove(id, out var opener);
		if (_store.Records.Count == 0)
		{
			Open(_blankUrl);
			return;
		}
		if (opener is not null && result.NextActiveId is not null && _store.Has(opener))
		{
			Activate(opener);
			return;
		}
		if (result.NextActiveId is not null)
		{
			Activate(result.NextActiveId);
			return;
		}
		_changed();
	}

	public void CloseOthers(string keep)
	{
		_store.Get(keep);
		foreach (var record in _store.Records.Where(r => r.Id != keep && !r.Pinned).ToList()) Close(record.Id);
		if (_store.ActiveId != keep) Activate(keep);
	}

	/// <summary>Loads a ready URL (the window has already resolved what was typed).</summary>
	public void Navigate(string id, string url)
	{
		var clean = LinkCleaner.StripTrackers(url);
		_store.Update(id, new TabContentPatch(Url: clean));
		if (_pages.TryGetValue(id, out var page)) page.Navigate(clean);
		_changed();
	}

	public void Move(string id, int index)
	{
		_store.Move(id, index);
		_changed();
	}

	/// <summary>Puts the tabs in exactly this order (pinned first); the arrangement and drag code decide what it is.</summary>
	public void ApplyOrder(IReadOnlyList<string> ids)
	{
		var current = _store.Records.Select(t => t.Id).ToList();
		if (ids.Count != current.Count) throw new TabOrderException($"{ids.Count} ids for {current.Count} tabs");
		if (ids.SequenceEqual(current)) return;
		for (var i = 0; i < ids.Count; i++) _store.Move(ids[i], i);
		if (!_store.Records.Select(t => t.Id).SequenceEqual(ids)) throw new TabOrderException("it puts a tab across the pinned divider");
		_changed();
	}

	public void Pin(string id)
	{
		_store.Pin(id);
		_changed();
	}

	public void Unpin(string id)
	{
		_store.Unpin(id);
		_changed();
	}

	public TabRecord? ReopenClosed()
	{
		var record = _store.ReopenClosed();
		if (record is null) return null;
		Activate(record.Id);
		return record;
	}

	public string? NextId() => _store.NextId();
	public string? PreviousId() => _store.PreviousId();
	public string? IdForShortcut(int n) => _store.IdForShortcut(n);

	public void FocusPage(string id) => _pages.GetValueOrDefault(id)?.Focus();

	/// <summary>Window teardown: every page goes, records go with the window.</summary>
	public void Destroy()
	{
		foreach (var id in _pages.Keys.ToList()) DestroyPage(id);
		foreach (var record in _store.Records) _siteData?.Release(record.Id);
	}

	private PageState? PageStateOf(string id) =>
		_pages.TryGetValue(id, out var page) ? new PageState(page.IsLoading, page.CanGoBack, page.CanGoForward) : null;

	private IPage EnsurePage(TabRecord record)
	{
		if (_pages.TryGetValue(record.Id, out var existing)) return existing;
		var page = _host.Create(new TabPageEvents(this, record.Id));
		_pages[record.Id] = page;
		page.Navigate(record.Url);
		if (_sleeping.Remove(record.Id)) Woke?.Invoke(record.Id, page);
		return page;
	}

	private void DestroyPage(string id)
	{
		if (!_pages.Remove(id, out var page)) return;
		_host.Hide(page);
		page.Close();
	}

	private void Update(string id, TabContentPatch patch)
	{
		if (!_store.Has(id)) return;
		_store.Update(id, patch);
		_changed();
	}

	private IPage AdoptPopup(string openerId, string url, bool background)
	{
		var record = _store.AddAfter(openerId, url);
		var page = _host.Create(new TabPageEvents(this, record.Id));
		_pages[record.Id] = page;
		_openers[record.Id] = openerId;
		if (background) _changed();
		else Activate(record.Id);
		return page;
	}

	private sealed class TabPageEvents(TabService service, string id) : IPageEvents
	{
		public NavigationDecision Navigating(string from, string to)
		{
			if (service._intercept(from, to) is { } decision)
			{
				// Like typing it: the tab shows where it is going even before (or if never) the page commits.
				if (decision is NavigationDecision.Redirect redirect) service.Update(id, new TabContentPatch(Url: redirect.Url));
				return decision;
			}
			var clean = LinkCleaner.StripTrackers(to);
			return clean == to ? NavigationDecision.Allow : new NavigationDecision.Redirect(clean);
		}

		public void Navigated(string url)
		{
			service._siteData?.Visit(id, url);
			service.Update(id, new TabContentPatch(Url: url));
		}

		public void TitleChanged(string title)
		{
			if (!service._store.Has(id)) return;
			var startPage = StartPage.IsStartPage(service._store.Get(id).Url);
			service.Update(id, new TabContentPatch(Title: string.IsNullOrEmpty(title) || startPage ? TabStore.NewTabTitle : title));
		}

		public void StateChanged() => service._changed();

		public void IconChanged(string? dataUrl)
		{
			if (!service._store.Has(id)) return;
			if (dataUrl is null) service._icons.Remove(id);
			else service._icons[id] = dataUrl;
			service._changed();
		}

		public void FindResult(int matches, int activeOrdinal)
		{
			if (service._pages.TryGetValue(id, out var page)) service.FindResult?.Invoke(page, matches, activeOrdinal);
		}

		public void LoadFailed(string description)
		{
			if (service._store.Has(id)) service._loadFailed(service._store.Get(id), description);
		}

		public void Closed()
		{
			if (!service._pages.Remove(id, out var page)) return;
			service._host.Hide(page);
			if (service._store.Has(id)) service.Close(id);
		}

		public IPage Popup(string url, bool background) => service.AdoptPopup(id, url, background);
	}
}
