namespace Zing.Core;

public sealed class UnknownPanelContentException(string id) : Exception($"{id} names no panel content")
{
	public string Id { get; } = id;
}

public sealed class UnknownSettingsCategoryException(string category) : Exception($"{category} is not a settings category")
{
	public string Category { get; } = category;
}

public sealed class UnknownShellMenuKindException(string kind) : Exception($"{kind} is not a shell menu kind")
{
	public string Kind { get; } = kind;
}

/// <summary>One row of a shell popup menu. A null <see cref="Invoke"/> is a separator.</summary>
public sealed record ShellMenuItem(string Label, Action? Invoke);

/// <summary>What the host tells the model it needs to know about the window, read fresh on each use.</summary>
/// <param name="PageArea">The page view's rectangle in window content coordinates: the window knows the tab bar position, Favourites bar and docked panels.</param>
/// <param name="ContentSize">The window's content size.</param>
/// <param name="ContentOrigin">Where the content area's top-left sits on screen, in the units the renderer's screen coordinates use.</param>
/// <param name="Cursor">The cursor relative to the content area, or null when it is outside it or Zing is unfocused (see <see cref="SidebarReveal"/>).</param>
public sealed record ShellWindow(Func<Rect> PageArea, Func<ContentSize> ContentSize, Func<Point> ContentOrigin, Func<Point?> Cursor);

/// <summary>
/// The pure decisions of the app shell (src/main/shell.ts): the settings sheet, the edge-reveal
/// sidebar, the floating tool panels, the account gate and the region of the window the surface
/// view covers. The host draws it: it listens to the events, and calls the methods that the
/// <c>shell:*</c> and <c>toolpanel:*</c> channels name. Single-threaded: call it, and give it a
/// scheduler, on the UI thread.
/// </summary>
public sealed class ShellModel : IDisposable
{
	public const string FullNotice = "Three panels are open. Minimize or close one first";

	private readonly string _appVersion;
	private readonly ShellWindow _window;
	private readonly ToolPanels _panels = new();
	private readonly SidebarReveal _reveal;
	private bool _settingsOpen;
	private bool _toolboxOpen;
	private string _category = ShellConstants.DefaultCategory;
	private bool _pinned;
	/// <summary>The next unpin came from the toggle (Ctrl+S, the sidebar's button): put the sidebar away, no peek.</summary>
	private bool _unpinQuietly;
	private List<string> _recents;
	private Rect? _region;

	public ShellModel(string appVersion, ShellWindow window, IScheduler scheduler, bool sidebarPinned, bool gated, IReadOnlyList<string> recentFeatures)
	{
		_appVersion = appVersion;
		_window = window;
		Gated = gated;
		_pinned = sidebarPinned;
		_recents = Known(recentFeatures);
		_reveal = new SidebarReveal(window.Cursor, scheduler);
		_reveal.Changed += _ =>
		{
			Sync();
			PushState();
		};
		_reveal.SetEnabled(!sidebarPinned);
	}

	/// <summary>The <c>shell:state</c> push: the surface view's state changed.</summary>
	public event Action<ShellState>? StateChanged;

	/// <summary>The <c>toolpanel:state</c> push.</summary>
	public event Action<ToolPanelsState>? PanelsChanged;

	/// <summary>
	/// The surface view's new rectangle in window content coordinates, or null to hide it. Raised on
	/// every recompute, not only on a change, so the host can also re-stack the view above the page.
	/// </summary>
	public event Action<Rect?>? SurfaceRegionChanged;

	/// <summary>The settings sheet opened or the gate covers the window: give the surface view keyboard focus.</summary>
	public event Action? FocusSurfaceRequested;

	/// <summary>The settings sheet closed: give the page keyboard focus back.</summary>
	public event Action? FocusPageRequested;

	/// <summary>The page view's rectangle changed (the sidebar was pinned or unpinned): lay the window out again.</summary>
	public event Action? RelayoutRequested;

	/// <summary>Show this text as a notice.</summary>
	public event Action<string>? NoticeRaised;

	/// <summary>The recently used list changed: store it as the <c>recentFeatures</c> setting.</summary>
	public event Action<IReadOnlyList<string>>? RecentFeaturesChanged;

	/// <summary>The user asked to pin or unpin the sidebar: store it as the <c>sidebarPinned</c> setting, then call <see cref="SetSidebarPinned"/>.</summary>
	public event Action<bool>? SidebarPinRequested;

	public bool Gated { get; private set; }

	public bool SidebarPinned => _pinned;

	public bool SidebarRevealed => _reveal.Revealed;

	public bool SettingsOpen => _settingsOpen;

	public bool ToolboxOpen => _toolboxOpen;

	public Rect? Region => _region;

	public IReadOnlyList<string> RecentFeatures => _recents;

	/// <summary>The column a pinned sidebar takes from the chrome; zero when it is hidden.</summary>
	public int PinnedWidth => !_pinned ? 0 : _collapsedWidth ?? SidebarWidth;

	private int? _collapsedWidth;

	/// <summary>
	/// The tab rail collapsed to icons (tabs on the left): it keeps that narrow column, and the full rail slides out over
	/// the page while the pointer is over the column, as a hidden sidebar does from the window's edge. Null when expanded.
	/// </summary>
	public void SetCollapsedWidth(int? width)
	{
		if (width == _collapsedWidth) return;
		_collapsedWidth = width;
		ApplyReveal();
		RelayoutRequested?.Invoke();
	}

	/// <summary>The sidebar slides out when it is hidden, or collapsed to icons; reaching for it means the edge, or the icon column.</summary>
	private void ApplyReveal()
	{
		_reveal.SetEdge(_pinned && _collapsedWidth is { } column ? column : EdgeReveal.DefaultEdge);
		_reveal.SetEnabled(!_pinned || _collapsedWidth is not null);
	}

	/// <summary>The left sidebar's width: the app sidebar's column, or the tab rail's when tabs are on the left (the app sidebar folds into it).</summary>
	public int SidebarWidth { get; private set; } = Layout.SidebarColumnWidth;

	public void SetSidebarWidth(int width)
	{
		if (width == SidebarWidth) return;
		SidebarWidth = width;
		_reveal.SetWidth(width);
		Sync();
		PushState();
	}

	public Rect PageRect() => _window.PageArea();

	private Rect? _dropZone;

	/// <summary>Where a tab dragged over a page would land in a split (split view), or null once the drag leaves or ends.</summary>
	public void SetDropZone(Rect? zone)
	{
		if (zone == _dropZone) return;
		_dropZone = zone;
		Sync();
		PushState();
	}

	public ShellState State() => new(
		_settingsOpen,
		_category,
		_toolboxOpen,
		_reveal.Revealed,
		Gated,
		new Point(_region?.X ?? 0, _region?.Y ?? 0),
		_appVersion,
		_dropZone);

	public ToolPanelsState PanelsState() => _panels.State();

	public void Dispose() => _reveal.Dispose();

	/// <summary>Call once the events are subscribed: computes the first surface region, and asks for focus if the gate covers the window.</summary>
	public void Start()
	{
		Sync();
		if (Gated) FocusSurfaceRequested?.Invoke();
	}

	// Window and settings changes

	/// <summary>After the window resizes or the page rectangle moves.</summary>
	public void LayoutChanged()
	{
		_panels.ClampAll(PageRect());
		Sync();
		PushPanels();
		// A view that has just been shown or moved needs the origin it now sits at.
		if (_region is not null) PushState();
	}

	/// <summary>The account state changed: <paramref name="signedIn"/> false means the gate covers the window.</summary>
	public void AccountChanged(bool signedIn)
	{
		Gated = !signedIn;
		Sync();
		PushState();
		FocusSurfaceIfCovering();
	}

	/// <summary>The <c>sidebarPinned</c> setting changed (or was first read).</summary>
	public void SetSidebarPinned(bool pinned)
	{
		if (_pinned == pinned) return;
		_pinned = pinned;
		ApplyReveal();
		// The page view's left edge moves with the sidebar.
		RelayoutRequested?.Invoke();
		// Unpinning from Settings peeks, to show where the sidebar went. The toggle means "put it away":
		// a peek there showed it again at once, and it stayed while the pointer was over it.
		if (!pinned && !_unpinQuietly) _reveal.Peek();
		_unpinQuietly = false;
	}

	/// <summary>The <c>recentFeatures</c> setting changed elsewhere.</summary>
	public void SetRecentFeatures(IReadOnlyList<string> recents) => _recents = Known(recents);

	/// <summary>A stored id can name something that no longer exists (a removed section): it is dropped, not shown.</summary>
	private static List<string> Known(IEnumerable<string> ids) => [.. ids.Where(id => PanelContent.FromId(id) is not null)];

	// Settings sheet

	public void OpenSettings(string? category = null)
	{
		if (category is not null)
		{
			if (!ShellConstants.IsSettingsCategory(category)) throw new UnknownSettingsCategoryException(category);
			_category = category;
		}
		_settingsOpen = true;
		Sync();
		PushState();
		FocusSurfaceRequested?.Invoke();
	}

	public void CloseSettings()
	{
		if (!_settingsOpen) return;
		_settingsOpen = false;
		Sync();
		PushState();
		FocusPageRequested?.Invoke();
	}

	// Sidebar

	public void ToggleSidebar()
	{
		_unpinQuietly = _pinned;
		SidebarPinRequested?.Invoke(!_pinned);
	}

	public void OpenToolbox()
	{
		_toolboxOpen = true;
		Sync();
		PushState();
		FocusSurfaceRequested?.Invoke();
	}

	public void CloseToolbox()
	{
		if (!_toolboxOpen) return;
		_toolboxOpen = false;
		Sync();
		PushState();
		FocusPageRequested?.Invoke();
	}

	// Tool panels

	/// <param name="screenPoint">Where the tear-off was released, in screen coordinates.</param>
	public OpenPanelOutcome OpenPanel(string contentId, Point screenPoint)
	{
		var content = Content(contentId);
		var origin = _window.ContentOrigin();
		var at = new Point(screenPoint.X - origin.X, screenPoint.Y - origin.Y);
		return AfterOpen(contentId, _panels.Open(content, at, PageRect()));
	}

	/// <summary>From a menu: lands in the middle of the page rather than under a pointer.</summary>
	public OpenPanelOutcome OpenPanelCentred(string contentId)
	{
		var content = Content(contentId);
		var page = PageRect();
		var at = new Point(page.X + page.Width / 2, page.Y + page.Height / 3);
		return AfterOpen(contentId, _panels.Open(content, at, page));
	}

	/// <summary>
	/// From the failed-load offer or another tool: opens (or raises) the panel with an address filled in. It only fills the field;
	/// starting anything is the person's click.
	/// </summary>
	public OpenPanelOutcome OpenPanelWithInput(string contentId, string input)
	{
		var content = Content(contentId);
		var page = PageRect();
		var at = new Point(page.X + page.Width / 2, page.Y + page.Height / 3);
		// No notice when it is full: the caller has its own place to say so, and a notice would replace the offer.
		var outcome = _panels.Open(content, at, page);
		if (outcome == OpenPanelOutcome.Full) return outcome;
		_panels.Hand(content, input);
		return AfterOpen(contentId, outcome);
	}

	/// <summary>A file opened from another app: like <see cref="OpenPanelWithInput"/>, but with no offer on screen to say so, a full set of panels is a notice.</summary>
	public OpenPanelOutcome OpenPanelWithInputOrSay(string contentId, string input)
	{
		var outcome = OpenPanelWithInput(contentId, input);
		if (outcome == OpenPanelOutcome.Full) NoticeRaised?.Invoke(FullNotice);
		return outcome;
	}

	/// <summary>The failed address of the offer on screen. While it is set, Open a Link and Diagnose open with it filled in, however they were opened.</summary>
	public string? OfferedAddress { get; set; }

	public void ClosePanel(string id)
	{
		_panels.Close(id);
		PanelsUpdated();
		FocusPageIfSurfaceHidden();
	}

	public void MinimizePanel(string id)
	{
		_panels.Minimize(id);
		PanelsUpdated();
		FocusPageIfSurfaceHidden();
	}

	public OpenPanelOutcome RestorePanel(string id) => AfterOpen(null, _panels.Restore(id, PageRect()));

	public void FocusPanel(string id)
	{
		_panels.BringToFront(id);
		PushPanels();
	}

	public void BeginPanelDrag(string id)
	{
		_panels.BeginDrag(id);
		PanelsUpdated();
	}

	public void EndPanelDrag(string id, Point origin)
	{
		_panels.EndDrag(id, origin, PageRect());
		PanelsUpdated();
	}

	/// <summary>The rows of the <c>shell:menu</c> popup for a recents row ("recent") or a minimized panel ("minimized").</summary>
	public IReadOnlyList<ShellMenuItem> MenuItems(string kind, string id) => kind switch
	{
		"recent" =>
		[
			new ShellMenuItem("Open in Window", () => OpenPanelCentred(id)),
			new ShellMenuItem("", null),
			new ShellMenuItem("Remove from Sidebar", () => ForgetRecent(id)),
		],
		"minimized" =>
		[
			new ShellMenuItem("Restore", () => RestorePanel(id)),
			new ShellMenuItem("Close", () => ClosePanel(id)),
		],
		_ => throw new UnknownShellMenuKindException(kind),
	};

	// Internals

	private static bool TakesOffer(PanelContent content) => content.Kind == PanelKind.Section && content.Name is PanelSection.Unblock or PanelSection.Diagnose;

	private static PanelContent Content(string id) => PanelContent.FromId(id) ?? throw new UnknownPanelContentException(id);

	private OpenPanelOutcome AfterOpen(string? contentId, OpenPanelOutcome outcome)
	{
		if (outcome == OpenPanelOutcome.Full)
		{
			NoticeRaised?.Invoke(FullNotice);
			return outcome;
		}
		if (contentId is not null) Remember(contentId);
		if (OfferedAddress is { } offered && contentId is not null && TakesOffer(Content(contentId)) && _panels.HasNoInput(Content(contentId))) _panels.Hand(Content(contentId), offered);
		// The sheet is how a tool was picked; the panel it opened is on the surface view already.
		_toolboxOpen = false;
		PanelsUpdated();
		return outcome;
	}

	private void Remember(string contentId)
	{
		// The Settings section is never a tool; only features go in the list.
		if (contentId == $"section:{PanelSection.Settings}") return;
		SetRecents(RecentList.Touching(contentId, _recents));
	}

	private void ForgetRecent(string id) => SetRecents(RecentList.Removing(id, _recents));

	private void SetRecents(List<string> recents)
	{
		_recents = recents;
		RecentFeaturesChanged?.Invoke(recents);
	}

	private void PanelsUpdated()
	{
		Sync();
		PushState();
		PushPanels();
	}

	/// <summary>A hidden view must not keep the keyboard: the panel that held it is gone.</summary>
	private void FocusPageIfSurfaceHidden()
	{
		if (_region is null) FocusPageRequested?.Invoke();
	}

	private void FocusSurfaceIfCovering()
	{
		if (!Gated && !_settingsOpen && !_toolboxOpen) return;
		FocusSurfaceRequested?.Invoke();
	}

	private void Sync()
	{
		_region = SurfaceRegion.Compute(new SurfaceInput(
			_window.ContentSize(),
			PageRect(),
			Gated,
			_settingsOpen,
			_reveal.Revealed,
			SidebarWidth,
			_panels.Visible.Select(p => p.Frame).ToList(),
			_panels.Dragging,
			_dropZone,
			_toolboxOpen));
		SurfaceRegionChanged?.Invoke(_region);
	}

	private void PushState() => StateChanged?.Invoke(State());

	private void PushPanels() => PanelsChanged?.Invoke(_panels.State());
}
