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
	public const string ToolboxNotice = "The Toolbox isn't implemented yet";
	public const string FullNotice = "Three panels are open. Minimize or close one first";

	private readonly string _appVersion;
	private readonly ShellWindow _window;
	private readonly ToolPanels _panels = new();
	private readonly SidebarReveal _reveal;
	private bool _settingsOpen;
	private string _category = ShellConstants.DefaultCategory;
	private bool _pinned;
	private List<string> _recents;
	private Rect? _region;

	public ShellModel(string appVersion, ShellWindow window, IScheduler scheduler, bool sidebarPinned, bool gated, IReadOnlyList<string> recentFeatures)
	{
		_appVersion = appVersion;
		_window = window;
		Gated = gated;
		_pinned = sidebarPinned;
		_recents = [.. recentFeatures];
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

	public Rect? Region => _region;

	public IReadOnlyList<string> RecentFeatures => _recents;

	/// <summary>The column a pinned sidebar takes from the chrome; zero when it is hidden.</summary>
	public int PinnedWidth => _pinned ? Layout.SidebarColumnWidth : 0;

	public Rect PageRect() => _window.PageArea();

	public ShellState State() => new(
		_settingsOpen,
		_category,
		_reveal.Revealed,
		Gated,
		new Point(_region?.X ?? 0, _region?.Y ?? 0),
		_appVersion);

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
		_reveal.SetEnabled(!pinned);
		// The page view's left edge moves with the sidebar.
		RelayoutRequested?.Invoke();
		if (!pinned) _reveal.Peek();
	}

	/// <summary>The <c>recentFeatures</c> setting changed elsewhere.</summary>
	public void SetRecentFeatures(IReadOnlyList<string> recents) => _recents = [.. recents];

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

	public void ToggleSidebar() => SidebarPinRequested?.Invoke(!_pinned);

	public void OpenToolbox() => NoticeRaised?.Invoke(ToolboxNotice);

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

	public void ClosePanel(string id)
	{
		_panels.Close(id);
		PanelsUpdated();
	}

	public void MinimizePanel(string id)
	{
		_panels.Minimize(id);
		PanelsUpdated();
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

	private static PanelContent Content(string id) => PanelContent.FromId(id) ?? throw new UnknownPanelContentException(id);

	private OpenPanelOutcome AfterOpen(string? contentId, OpenPanelOutcome outcome)
	{
		if (outcome == OpenPanelOutcome.Full)
		{
			NoticeRaised?.Invoke(FullNotice);
			return outcome;
		}
		if (contentId is not null) Remember(contentId);
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

	private void FocusSurfaceIfCovering()
	{
		if (!Gated && !_settingsOpen) return;
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
			_panels.Visible.Select(p => p.Frame).ToList(),
			_panels.Dragging));
		SurfaceRegionChanged?.Invoke(_region);
	}

	private void PushState() => StateChanged?.Invoke(State());

	private void PushPanels() => PanelsChanged?.Invoke(_panels.State());
}
