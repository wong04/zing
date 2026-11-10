using Zing.Core.LinkPreview;
using Zing.Core.Net;

namespace Zing.Core;

/// <summary>A tab's live page. The Win32 host implements it over a WebView2; tests use a fake.</summary>
public interface IPage
{
	string Url { get; }
	string Title { get; }
	bool IsLoading { get; }
	bool CanGoBack { get; }
	bool CanGoForward { get; }
	/// <summary>Whether an element of the page is in HTML fullscreen: Esc is then the page's, to leave it.</summary>
	bool IsFullScreen { get; }
	/// <summary>Whether a frame of the page shows a JavaScript dialog (alert, confirm, prompt, beforeunload): Esc is then the dialog's.</summary>
	bool HasOpenDialog { get; }
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
	/// <summary>The tab's page zoom factor (1 is actual size); the engine's Ctrl+wheel changes it too.</summary>
	double Zoom { get; set; }
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
	/// <summary>Sends the page's events somewhere else from now on, e.g. when an expanded link preview becomes a tab.</summary>
	void Rebind(IPageEvents events);
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
	NavigationDecision Navigating(string from, string to, NavigationCause cause);
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
	/// <summary>The page took the keyboard, e.g. a click into a split pane: that pane's tab becomes the active one.</summary>
	void Focused();
	/// <summary>The page opened a window; the returned page must be the one handed to the engine, so window.opener works.</summary>
	IPage Popup(PopupRequest request);
	/// <summary>
	/// The page's render process exited or stopped answering. The tab keeps its record and its
	/// page; the chrome offers Reload, and the next document the page starts clears the fault.
	/// </summary>
	void Crashed(PageFault fault);
	/// <summary>Something the person should be told about this page that isn't a failure to load, e.g. a relayed web app that can't work.</summary>
	void Notice(string text);
	/// <summary>The page's autofill script posted <paramref name="message"/>; <paramref name="frameSource"/> is the engine's own Source for it, never anything the page said.</summary>
	void Autofill(Uri frameSource, Vault.AutofillMessage message);
	/// <summary>A new document has started to load in the page: a link, Back or Forward, a reload, or the page's own script. Same-document navigations don't count.</summary>
	void NewDocument();
	/// <summary>The engine took this page as the window another page asked for, and starts loading it.</summary>
	void WindowOpened();
}

/// <summary>Makes pages and puts them on screen. The window owns layout and stacking.</summary>
public interface IPageHost
{
	IPage Create(IPageEvents events);
	void Show(IPage page);
	/// <summary>Shows a split's pages, in pane order, laid out by <paramref name="layout"/>; <paramref name="focused"/> is the active tab's.</summary>
	void ShowSplit(IReadOnlyList<IPage> pages, SplitLayout layout, IPage focused);
	void Hide(IPage page);
	/// <summary>Shows a link preview's pages over the tab pages, bottom first; an empty list removes the preview.</summary>
	void ShowPreview(IReadOnlyList<IPage> layers);
	/// <summary>Gives the keyboard to the preview's top page.</summary>
	void FocusPreview();
}

/// <summary>A page asking for a new window: where to, the keys or button that asked, and whether it gave the window a size or position.</summary>
public sealed record PopupRequest(string Url, Modifiers Modifiers, bool HasPopupFeatures);

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
	private readonly Func<string, string, string, NavigationDecision?> _intercept;
	private readonly SiteDataKeeper? _siteData;
	private readonly Action<TabRecord, string> _pageNotice;
	private readonly Func<PeekSettings>? _peekSettings;
	private readonly IScheduler? _scheduler;
	private readonly Func<bool> _blockTrackers;
	/// <summary>New windows held, hidden, until their first real document says whether they are a preview or a tab; each with its timer once the engine has the window.</summary>
	private readonly Dictionary<PendingPopup, IDisposable?> _pending = [];
	/// <summary>The icons of the open preview's pages, kept for the tab an expanded one becomes.</summary>
	private readonly Dictionary<IPage, string> _previewIcons = [];

	/// <summary>How long a held new window may go without a real document, from when the engine has it, before it opens as a tab, as it did before previews.</summary>
	private static readonly TimeSpan PendingPopupWait = TimeSpan.FromSeconds(1);

	/// <summary>A page reported find results; the window's find bar decides whether they are for it.</summary>
	public event Action<IPage, int, int>? FindResult;

	/// <summary>A tab's autofill script reported a sign-in field (Vault.Autofill decides what it gets).</summary>
	public event Action<string, Uri, Vault.AutofillMessage>? AutofillMessage;
	private readonly Dictionary<string, IPage> _pages = new(StringComparer.Ordinal);
	private readonly Dictionary<string, string> _openers = new(StringComparer.Ordinal);
	private readonly HashSet<string> _sleeping = new(StringComparer.Ordinal);
	private readonly Dictionary<string, string> _icons = new(StringComparer.Ordinal);
	private readonly Dictionary<string, PageFault> _faults = new(StringComparer.Ordinal);

	/// <summary>A sleeping tab just got a fresh page, so its saved scroll can be put back.</summary>
	public event Action<string, IPage>? Woke;

	/// <summary>A tab's page is about to leave the screen: the last moment it can be pictured.</summary>
	public event Action<string, IPage>? Leaving;

	/// <summary>A tab closed for good.</summary>
	public event Action<string>? Closed;

	/// <summary>The link preview open over the active tab, or null.</summary>
	public PeekSession? Preview { get; private set; }

	/// <summary>A preview opened, closed, changed its layers, or its page moved on or retitled.</summary>
	public event Action? PreviewChanged;

	/// <summary>A preview layer's page failed to load: its own address, and why.</summary>
	public event Action<string, string>? PreviewLoadFailed;

	/// <param name="blankUrl">What a window that lost its last tab opens.</param>
	/// <param name="intercept">The window's say over a navigation (the start page's search and settings links); null leaves it to tracker stripping.</param>
	/// <param name="peekSettings">The link preview settings, read at each link; null means links never preview.</param>
	/// <param name="scheduler">Makes held new windows that load no document tabs; null leaves them until their opener's tab closes.</param>
	/// <param name="blockTrackers">The blockTrackers setting, read at each navigation; null means tracking parameters are always stripped.</param>
	public TabService(TabStore store, IPageHost host, string blankUrl, Action changed, Action<TabRecord, string> loadFailed, Func<string, string, string, NavigationDecision?>? intercept = null, SiteDataKeeper? siteData = null, Action<TabRecord, string>? pageNotice = null, Func<PeekSettings>? peekSettings = null, IScheduler? scheduler = null, Func<bool>? blockTrackers = null)
	{
		_blockTrackers = blockTrackers ?? (() => true);
		_peekSettings = peekSettings;
		_scheduler = scheduler;
		_pageNotice = pageNotice ?? ((_, _) => { });
		_siteData = siteData;
		_intercept = intercept ?? ((_, _, _) => null);
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
		return state with { Tabs = [.. state.Tabs.Select(t => t with { Sleeping = _sleeping.Contains(t.Id), Icon = _icons.GetValueOrDefault(t.Id), PageState = FaultOf(t.Id) is { } fault ? PageFaults.Name(fault) : null })] };
	}

	public bool IsSleeping(string id) => _sleeping.Contains(id);

	/// <summary>Why the tab's page isn't working, or null while it is (or has no page).</summary>
	public PageFault? FaultOf(string id) => _faults.TryGetValue(id, out var fault) ? fault : null;

	/// <summary>Reloads the tab's page, which starts a crashed or hung one afresh at its URL.</summary>
	public void Reload(string id)
	{
		_store.Get(id);
		if (!_pages.TryGetValue(id, out var page)) return;
		ClearFault(id);
		page.Reload();
	}

	/// <summary>Drops the page but keeps the record, so the tab wakes at its URL when next activated.</summary>
	public void Sleep(string id)
	{
		_store.Get(id);
		if (IsShown(id) || Preview?.SourceTabId == id) throw new ActiveTabSleepException(id);
		if (!_pages.ContainsKey(id)) return;
		DestroyPage(id);
		_sleeping.Add(id);
		_changed();
	}

	public bool Has(string id) => _store.Has(id);

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

	/// <summary>
	/// A new tab on a page fetched through the relay (Open a Link): it loads on the relay scheme, so its address bar says so.
	/// The tab's navigation is one Zing starts, which is the only kind the relay scheme accepts.
	/// </summary>
	/// <param name="url">The real address, as Open a Link was given it.</param>
	public TabRecord OpenRelayed(string url)
	{
		var web = RelayRules.Normalize(url);
		var relay = RelayedContent.Encode(web) ?? throw new RelayedAddressError();
		return Open(relay.AbsoluteUri);
	}

	/// <summary>Brings back saved tabs as records; only the one that was in front gets a page.</summary>
	public void Restore(IReadOnlyList<SessionTab> tabs)
	{
		var records = tabs.Select(t => _store.AddRestored(t.Url, string.IsNullOrEmpty(t.Title) ? TabStore.NewTabTitle : t.Title, t.Pinned, t.ItemId)).ToList();
		if (records.Count == 0) return;
		foreach (var split in Session.SplitsOf(tabs, [.. records.Select(r => r.Id)])) Splits.Put(split);
		var front = tabs.ToList().FindIndex(t => t.Active);
		Activate(records[Math.Max(0, front)].Id);
	}

	public void Activate(string id)
	{
		// Decision 6: switching tabs closes the preview.
		if (Preview is { } preview && preview.SourceTabId != id) ClosePreview();
		var previous = _store.ActiveId;
		var shownBefore = previous is null ? [] : Splits.GroupOf(previous);
		_store.Activate(id);
		if (previous is not null && previous != id)
		{
			// Moving between panes of one split hides nothing; leaving it hides every pane.
			HideAllBut(shownBefore, Splits.GroupOf(id));
			if (_store.Has(previous)) _store.Touch(previous);
		}
		ShowActive();
		_changed();
	}

	/// <summary>This window's splits (split view).</summary>
	public SplitSet Splits { get; } = new();

	/// <summary>Whether the tab's page is on screen: the active tab, or a pane of its split.</summary>
	public bool IsShown(string id) => _store.ActiveId is { } active && Splits.GroupOf(active).Contains(id);

	/// <summary>
	/// Puts <paramref name="other"/> beside <paramref name="id"/>: into its split as the last pane, or a new side-by-side
	/// split of the two. The new pane gets the focus.
	/// </summary>
	public void SplitWith(string id, string other)
	{
		_store.Get(id);
		_store.Get(other);
		if (id == other) throw new SplitException("A tab can't be split with itself");
		Splits.Remove(other);
		var layout = Splits.Of(id) is { } split
			? Split.AddPane(split, other, split.TabIds.Count)
			: Split.Create(Guid.NewGuid().ToString(), [id, other], "row");
		Splits.Put(layout);
		Activate(other);
	}

	/// <summary>A tab dropped onto a page's edge: beside that page, on that side.</summary>
	public void SplitDrop(string targetId, string droppedId, string edge)
	{
		_store.Get(targetId);
		_store.Get(droppedId);
		if (targetId == droppedId) return;
		Splits.Remove(droppedId);
		var after = edge is "right" or "bottom";
		var layout = Splits.Of(targetId) is { } split
			? Split.AddPane(split, droppedId, split.TabIds.ToList().IndexOf(targetId) + (after ? 1 : 0))
			: Split.FromDrop(Guid.NewGuid().ToString(), targetId, droppedId, edge);
		Splits.Put(layout);
		Activate(droppedId);
	}

	/// <summary>Takes a tab out of its split; the rest stay together (a split of one dissolves).</summary>
	public void Unsplit(string id)
	{
		var shownBefore = _store.ActiveId is { } active ? Splits.GroupOf(active) : [];
		if (!Splits.Remove(id)) return;
		Reshow(shownBefore);
	}

	/// <summary>New pane sizes, from a divider dragged in the chrome; <paramref name="containerLength"/> is the page area's along the split.</summary>
	public void SetSplitRatios(string splitId, IReadOnlyList<double> ratios, double containerLength)
	{
		Splits.Put(Split.WithRatios(Splits.Get(splitId), ratios, containerLength));
		if (_store.ActiveId is { } active && Splits.Of(active)?.Id == splitId) ShowActive();
		_changed();
	}

	/// <summary>After a split changed: hide what is no longer shown, and show the active tab's group.</summary>
	private void Reshow(IReadOnlyList<string> shownBefore)
	{
		if (_store.ActiveId is not { } active) return;
		HideAllBut(shownBefore, Splits.GroupOf(active));
		ShowActive();
		_changed();
	}

	private void HideAllBut(IReadOnlyList<string> shown, IReadOnlyList<string> keep)
	{
		foreach (var id in shown.Where(t => !keep.Contains(t)))
		{
			if (!_pages.TryGetValue(id, out var leaving)) continue;
			Leaving?.Invoke(id, leaving);
			_host.Hide(leaving);
		}
	}

	/// <summary>The active tab's page, or every pane of its split.</summary>
	private void ShowActive()
	{
		var active = _store.Get(_store.ActiveId ?? throw new InvalidOperationException("No active tab"));
		if (Splits.Of(active.Id) is not { } split)
		{
			_host.Show(EnsurePage(active));
			return;
		}
		var pages = split.TabIds.Select(t => EnsurePage(_store.Get(t))).ToList();
		_host.ShowSplit(pages, split, pages[split.TabIds.ToList().IndexOf(active.Id)]);
	}

	/// <summary>Closing the last tab leaves a fresh one, as in Krackn: a window is never tabless.</summary>
	public void Close(string id)
	{
		if (Preview?.SourceTabId == id) ClosePreview();
		foreach (var held in _pending.Keys.Where(p => p.OpenerTabId == id).ToList()) DropPending(held);
		var shownBefore = _store.ActiveId is { } active ? Splits.GroupOf(active) : [];
		var wasActive = id == _store.ActiveId;
		var leftSplit = Splits.Remove(id);
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
		if (result.NextActiveId is not null)
		{
			var next = opener is not null && _store.Has(opener) ? opener : result.NextActiveId;
			// The store has already made its pick active, so Activate can't tell what was on screen: the closed tab's
			// split partners are hidden here.
			HideAllBut(shownBefore, Splits.GroupOf(next));
			Activate(next);
			return;
		}
		// A pane closed beside the active tab: what is left of the split is laid out again.
		if (leftSplit && !wasActive)
		{
			Reshow(shownBefore);
			return;
		}
		_changed();
	}

	public void CloseOthers(string keep)
	{
		_store.Get(keep);
		// Kept tab first, so closing the others doesn't give each neighbour a page in turn.
		if (_store.ActiveId != keep) Activate(keep);
		foreach (var record in _store.Records.Where(r => r.Id != keep && !r.Pinned).ToList()) Close(record.Id);
		if (_store.ActiveId != keep) Activate(keep);
	}

	/// <summary>The URL without its tracking parameters while blockTrackers is on (Krackn's BrowserTab.load), else as given.</summary>
	public string CleanLink(string url) => _blockTrackers() ? LinkCleaner.StripTrackers(url) : url;

	/// <summary>Loads a ready URL (the window has already resolved what was typed).</summary>
	public void Navigate(string id, string url)
	{
		if (Preview?.SourceTabId == id) ClosePreview();
		var clean = CleanLink(url);
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
		if (ids.Distinct().Count() != ids.Count || ids.Any(id => !_store.Has(id))) throw new TabOrderException("it repeats a tab or names one that isn't open");
		var pinned = _store.Records.Count(t => t.Pinned);
		if (ids.Take(pinned).Any(id => !_store.Get(id).Pinned)) throw new TabOrderException("it puts a tab across the pinned divider");
		for (var i = 0; i < ids.Count; i++) _store.Move(ids[i], i);
		_changed();
	}

	public void Pin(string id)
	{
		_store.Pin(id);
		_changed();
	}

	/// <summary>Ties a tab to a saved item (SavedTabs).</summary>
	public void Bind(string id, string itemId)
	{
		_store.Bind(id, itemId);
		_changed();
	}

	/// <summary>Unties a tab from its item; the tab stays open.</summary>
	public void Unbind(string id)
	{
		_store.Unbind(id);
		_changed();
	}

	public string? IdForItem(string itemId) => _store.IdForItem(itemId);

	/// <summary>A saved item's tab: the one it has in this window, or a new one at its address, brought to the front.</summary>
	public void OpenItem(string itemId, string url)
	{
		if (_store.IdForItem(itemId) is { } existing)
		{
			Activate(existing);
			return;
		}
		var record = _store.Add(url);
		_store.Bind(record.Id, itemId);
		Activate(record.Id);
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
		foreach (var layer in Preview?.CloseAll() ?? []) layer.Page.Close();
		Preview = null;
		_previewIcons.Clear();
		foreach (var held in _pending.Keys.ToList()) DropPending(held);
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
		// A fault belongs to the page, not the record: a woken tab starts with a working one.
		_faults.Remove(id);
		if (!_pages.Remove(id, out var page)) return;
		_host.Hide(page);
		page.Close();
	}

	private void ClearFault(string id)
	{
		if (_faults.Remove(id)) _changed();
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
		Attach(record, page, openerId, background);
		return page;
	}

	/// <summary>A page that already exists becomes the tab's, which remembers its opener so closing it goes back there.</summary>
	private void Attach(TabRecord record, IPage page, string openerId, bool background)
	{
		_pages[record.Id] = page;
		_openers[record.Id] = openerId;
		if (background) _changed();
		else Activate(record.Id);
	}

	// MARK: link previews

	/// <summary>What a link from a tab does. Only the active tab previews: a preview sits over the tab in front.</summary>
	private PeekVerdict Verdict(string id, LinkRequest request)
	{
		if (_peekSettings is null || _store.ActiveId != id) return PeekVerdict.Normal;
		return PeekRules.Decide(request, _peekSettings());
	}

	/// <summary>A window a tab's page asked for: a preview, a held window that decides on its first document, or a tab as before.</summary>
	private IPage TabPopup(string openerId, PopupRequest request)
	{
		var link = new LinkRequest(_store.Get(openerId).Url, request.Url, LinkTrigger.NewWindow, NavigationCause.Other, request.Modifiers, request.HasPopupFeatures, FromPreview: false);
		switch (Verdict(openerId, link))
		{
			case PeekVerdict.Preview:
				var page = CreatePreviewPage(openerId);
				StartPreview(openerId, page, request.Url);
				return page;
			case PeekVerdict.Undecided:
				return HoldPopup(openerId, link);
			default:
				return AdoptPopup(openerId, request.Url, OpensBehind(request.Modifiers));
		}
	}

	/// <summary>Ctrl (with or without Shift) and the middle button open a window as a tab behind the one in front.</summary>
	private static bool OpensBehind(Modifiers modifiers) => modifiers.Ctrl || modifiers.Middle;

	private IPage CreatePreviewPage(string sourceId)
	{
		var events = new PeekPageEvents(this, sourceId);
		var page = _host.Create(events);
		events.Page = page;
		return page;
	}

	/// <summary>Opens a preview of <paramref name="page"/> over the source tab, in place of any preview already open.</summary>
	/// <param name="url">Where the preview is going, which its page may not have committed yet.</param>
	private void StartPreview(string sourceId, IPage page, string url)
	{
		ClosePreview();
		var session = new PeekSession(sourceId);
		session.Push(page, isPopup: false, url);
		Preview = session;
		ShowPreview();
	}

	private void ShowPreview()
	{
		var session = Preview ?? throw new InvalidOperationException("No preview is open");
		_host.ShowPreview([.. session.Layers.Select(l => l.Page)]);
		_host.FocusPreview();
		PreviewChanged?.Invoke();
	}

	/// <summary>Closes the whole preview; its page goes onto the reopen-closed stack as an ordinary tab (decision 7).</summary>
	public void ClosePreview()
	{
		if (Preview is not { } session) return;
		var layers = session.CloseAll();
		Preview = null;
		_previewIcons.Clear();
		RememberClosed(layers[^1].Page);
		_host.ShowPreview([]);
		foreach (var layer in layers) layer.Page.Close();
		PreviewChanged?.Invoke();
		_changed();
	}

	/// <summary>Closes the preview's top popup layer, or the whole preview when only its page is left.</summary>
	public void CloseTopLayer()
	{
		var session = Preview ?? throw new InvalidOperationException("No preview is open");
		if (session.Layers.Count == 1)
		{
			ClosePreview();
			return;
		}
		ClosePage(session.Pop());
		ShowPreview();
	}

	/// <summary>The preview's page becomes a tab right after the source, without reloading; closing that tab goes back to the source.</summary>
	public TabRecord ExpandPreview()
	{
		var session = Preview ?? throw new InvalidOperationException("No preview is open");
		if (!session.CanExpand) throw new InvalidOperationException("A preview can't be expanded while a popup is over it");
		var page = session.CloseAll()[0].Page;
		Preview = null;
		var record = _store.AddAfter(session.SourceTabId, page.Url);
		if (!string.IsNullOrEmpty(page.Title)) record = _store.Update(record.Id, new TabContentPatch(Title: page.Title));
		if (_previewIcons.TryGetValue(page, out var icon)) _icons[record.Id] = icon;
		_previewIcons.Clear();
		page.Rebind(new TabPageEvents(this, record.Id));
		Attach(record, page, session.SourceTabId, background: false);
		// After the tab is shown, so the host keeps the page on screen and gives it the keyboard.
		_host.ShowPreview([]);
		PreviewChanged?.Invoke();
		return _store.Get(record.Id);
	}

	/// <summary>A preview page that goes away is offered on the reopen-closed stack as an ordinary tab (decision 7).</summary>
	private void RememberClosed(IPage page) =>
		_store.PushClosed(page.Url, string.IsNullOrEmpty(page.Title) ? TabStore.NewTabTitle : page.Title);

	private int LayerIndexOf(IPage page) => Preview?.Layers.ToList().FindIndex(l => l.Page == page) ?? -1;

	private PeekLayer? LayerOf(IPage page) => Preview?.Layers.FirstOrDefault(l => l.Page == page);

	/// <summary>A popup layer taken off the preview: its page goes, and its icon with it.</summary>
	private void ClosePage(PeekLayer layer)
	{
		_previewIcons.Remove(layer.Page);
		layer.Page.Close();
	}

	/// <summary>A preview layer's page closed itself: its layer goes, with any popups above it, or the whole preview if it was the page.</summary>
	private void PreviewPageClosed(IPage page)
	{
		if (Preview is not { } session) return;
		var index = LayerIndexOf(page);
		if (index < 0) return;
		if (index == 0)
		{
			ClosePreview();
			return;
		}
		while (session.Layers.Count > index) ClosePage(session.Pop());
		ShowPreview();
	}

	/// <summary>
	/// A preview has no Reload, so a layer whose renderer crashed closes like one that closed itself, and its address goes
	/// onto the reopen-closed stack so Ctrl+Shift+T brings it back as a tab. (The bottom layer gets there through ClosePreview.)
	/// </summary>
	private void PreviewPageCrashed(IPage page)
	{
		if (LayerIndexOf(page) > 0) RememberClosed(page);
		PreviewPageClosed(page);
	}

	/// <summary>
	/// A window from a preview page: with Ctrl or the middle button a tab behind, as from a tab; otherwise it stacks over
	/// the preview, so window.opener works (OAuth, payments).
	/// </summary>
	private IPage PreviewPopup(IPage opener, PopupRequest request)
	{
		if (Preview is not { } session || !session.Layers.Any(l => l.Page == opener)) throw new InvalidOperationException("The preview that asked for a window has closed");
		if (OpensBehind(request.Modifiers)) return AdoptPopup(session.SourceTabId, request.Url, background: true);
		var page = CreatePreviewPage(session.SourceTabId);
		session.Push(page, isPopup: true, request.Url);
		ShowPreview();
		return page;
	}

	private IPage HoldPopup(string openerId, LinkRequest link)
	{
		var background = OpensBehind(link.Modifiers);
		var events = new PendingPageEvents(this, link.To, background);
		var page = _host.Create(events);
		var held = new PendingPopup(page, openerId, to => Verdict(openerId, link with { To = to }));
		events.Held = held;
		// No timer yet: the wait starts once the engine has the window (WindowOpened).
		_pending[held] = null;
		return page;
	}

	/// <summary>
	/// A held window the engine now has: one that shows no real document soon (one a script writes into) is a tab, as before.
	/// Counted from here, not from the request, so a slow engine start doesn't eat the time a page has to set the location.
	/// With no scheduler it becomes a tab only on its first http(s) navigation.
	/// </summary>
	private void StartPendingWait(PendingPopup held, string url, bool background)
	{
		if (_scheduler is null || !_pending.TryGetValue(held, out var timer) || timer is not null) return;
		_pending[held] = _scheduler.Schedule(PendingPopupWait, () => SettleAsTab(held, url, background));
	}

	/// <summary>Stops holding the window; false when it was no longer held.</summary>
	private bool Release(PendingPopup held)
	{
		if (!_pending.Remove(held, out var timer)) return false;
		timer?.Dispose();
		return true;
	}

	private void DropPending(PendingPopup held)
	{
		if (Release(held)) held.Page.Close();
	}

	/// <summary>A held window's first real document decided it: it becomes a preview or a tab, and only then shows. Returns the page's new events.</summary>
	private IPageEvents Settle(PendingPopup held, PeekVerdict verdict, string to, bool background) =>
		verdict == PeekVerdict.Preview ? SettleAsPreview(held, to) : SettleAsTab(held, to, background);

	private IPageEvents SettleAsPreview(PendingPopup held, string to)
	{
		Release(held);
		var events = new PeekPageEvents(this, held.OpenerTabId) { Page = held.Page };
		held.Page.Rebind(events);
		StartPreview(held.OpenerTabId, held.Page, to);
		return events;
	}

	/// <summary>A tab beside its opener; behind it when asked, or when the person has gone to another tab meanwhile.</summary>
	private IPageEvents SettleAsTab(PendingPopup held, string url, bool background)
	{
		Release(held);
		var record = _store.AddAfter(held.OpenerTabId, url);
		var events = new TabPageEvents(this, record.Id);
		held.Page.Rebind(events);
		Attach(record, held.Page, held.OpenerTabId, background || _store.ActiveId != held.OpenerTabId);
		return events;
	}

	private sealed class TabPageEvents(TabService service, string id) : IPageEvents
	{
		public void Focused()
		{
			if (service._store.Has(id) && service._store.ActiveId != id && service.IsShown(id)) service.Activate(id);
		}

		public NavigationDecision Navigating(string from, string to, NavigationCause cause)
		{
			var decision = Decide(from, to, cause);
			// A new document is starting, so the page has a working renderer again (a reload, a link, Back).
			if (decision is NavigationDecision.Allowed) service.ClearFault(id);
			return decision;
		}

		private NavigationDecision Decide(string from, string to, NavigationCause cause)
		{
			if (service._intercept(id, from, to) is { } decision)
			{
				// Like typing it: the tab shows where it is going even before (or if never) the page commits.
				if (decision is NavigationDecision.Redirect redirect) service.Update(id, new TabContentPatch(Url: redirect.Url));
				return decision;
			}
			var clean = service.CleanLink(to);
			var link = new LinkRequest(from, to, LinkTrigger.Navigation, cause, default, HasPopupFeatures: false, FromPreview: false);
			if (service.Verdict(id, link) == PeekVerdict.Preview)
			{
				// The click stays out of the tab and loads in a preview over it.
				var page = service.CreatePreviewPage(id);
				page.Navigate(clean);
				service.StartPreview(id, page, clean);
				return NavigationDecision.Cancel;
			}
			return clean == to ? NavigationDecision.Allow : new NavigationDecision.Redirect(clean);
		}

		public void Crashed(PageFault fault)
		{
			// Only this tab's current page counts: one that closed or slept meanwhile has nothing to reload.
			if (!service._store.Has(id) || !service._pages.ContainsKey(id)) return;
			var current = service.FaultOf(id);
			// A crash outranks a hang, and a hang reported again changes nothing.
			if (current == fault || current == PageFault.Crashed) return;
			service._faults[id] = fault;
			service._changed();
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

		public void Notice(string text)
		{
			if (service._store.Has(id)) service._pageNotice(service._store.Get(id), text);
		}

		public IPage Popup(PopupRequest request) => service.TabPopup(id, request);

		public void Autofill(Uri frameSource, Vault.AutofillMessage message)
		{
			if (service._pages.ContainsKey(id)) service.AutofillMessage?.Invoke(id, frameSource, message);
		}

		/// <summary>Navigating the source tab closes its preview, however the navigation started (Back, a reload, the page's own script).</summary>
		public void NewDocument()
		{
			if (service.Preview?.SourceTabId == id) service.ClosePreview();
		}

		public void WindowOpened() { }
	}

	/// <summary>What a preview layer's page reports: it navigates itself, stacks its popups and closes its own layer.</summary>
	private sealed class PeekPageEvents(TabService service, string sourceId) : IPageEvents
	{
		public IPage? Page { get; set; }

		private IPage Own => Page ?? throw new InvalidOperationException("The preview page's events have no page yet");

		/// <summary>Links stay in the preview (no nested previews), inspected and cleaned as a tab's are.</summary>
		public NavigationDecision Navigating(string from, string to, NavigationCause cause)
		{
			// Inspection is keyed by the source tab, so without it the link can't be inspected: refuse rather than fail open.
			if (!service._store.Has(sourceId)) return NavigationDecision.Cancel;
			if (service._intercept(sourceId, from, to) is { } decision) return decision;
			var clean = service.CleanLink(to);
			return clean == to ? NavigationDecision.Allow : new NavigationDecision.Redirect(clean);
		}

		public void Navigated(string url) => service.PreviewChanged?.Invoke();
		public void TitleChanged(string title) => service.PreviewChanged?.Invoke();
		/// <summary>Kept for the tab the preview may become (Expand).</summary>
		public void IconChanged(string? dataUrl)
		{
			if (service.LayerOf(Own) is null) return;
			if (dataUrl is null) service._previewIcons.Remove(Own);
			else service._previewIcons[Own] = dataUrl;
		}

		public void StateChanged() { }

		/// <summary>Told about by the preview's own address: the source tab's page is fine, so nothing may be offered for it.</summary>
		public void LoadFailed(string description)
		{
			if (service.LayerOf(Own) is { } layer) service.PreviewLoadFailed?.Invoke(layer.Address, description);
		}

		public void FindResult(int matches, int activeOrdinal) { }
		public void Closed() => service.PreviewPageClosed(Own);
		public void Focused() { }
		public IPage Popup(PopupRequest request) => service.PreviewPopup(Own, request);

		/// <summary>A crash closes the layer; a hang is left alone, since it may recover and a preview has no Reload to offer.</summary>
		public void Crashed(PageFault fault)
		{
			if (fault == PageFault.Crashed) service.PreviewPageCrashed(Own);
		}

		public void Notice(string text)
		{
			if (service._store.Has(sourceId)) service._pageNotice(service._store.Get(sourceId), text);
		}

		public void Autofill(Uri frameSource, Vault.AutofillMessage message) { }
		public void NewDocument() { }
		public void WindowOpened() { }
	}

	/// <summary>What a held new window reports until its first real document decides where it goes.</summary>
	/// <param name="url">The address the window was asked for (about:blank), which a tab it becomes starts on.</param>
	/// <param name="background">Whether the window, if it becomes a tab, stays behind the opener (Ctrl or middle click).</param>
	private sealed class PendingPageEvents(TabService service, string url, bool background) : IPageEvents
	{
		public PendingPopup? Held { get; set; }

		private PendingPopup Own => Held ?? throw new InvalidOperationException("The held window's events have no window yet");

		public NavigationDecision Navigating(string from, string to, NavigationCause cause)
		{
			if (!service._pending.ContainsKey(Own)) return NavigationDecision.Cancel;
			if (Own.Navigating(to) is not { } verdict) return NavigationDecision.Allow;
			return service.Settle(Own, verdict, to, background).Navigating(from, to, cause);
		}

		public void Navigated(string url) { }
		public void TitleChanged(string title) { }
		public void IconChanged(string? dataUrl) { }
		public void StateChanged() { }
		public void LoadFailed(string description) { }
		public void FindResult(int matches, int activeOrdinal) { }
		public void Closed() => service.DropPending(Own);
		public void Focused() { }
		/// <summary>A window opened before this one showed anything belongs beside the opener's tab, as before.</summary>
		public IPage Popup(PopupRequest request) => service.AdoptPopup(Own.OpenerTabId, request.Url, OpensBehind(request.Modifiers));
		public void Crashed(PageFault fault) { }
		public void Notice(string text) { }
		public void Autofill(Uri frameSource, Vault.AutofillMessage message) { }
		public void NewDocument() { }
		public void WindowOpened() => service.StartPendingWait(Own, url, background);
	}
}
