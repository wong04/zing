using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Zing.Core;
using Zing.Core.Net;

namespace Zing.Host;

/// <summary>
/// One browser window (zing-window.ts): the chrome renderer filling the window, the
/// active tab's page over it, and above them the overlays, each in a popup of its own
/// (the settings surface, notices, page UI, the stack dropdown and the overview).
/// The composition root: it creates the window's parts and implements the host interfaces by handing
/// each call to the part that owns it (WindowLayout, OverlayHost, DownloadHost, PermissionHost, the layers).
/// </summary>
public sealed class MainWindow : IWindowHost, IPageHost, IPageFeatureHost
{
	// First: everything the window starts is tracked by it, and closes with it.
	private readonly WindowScope _scope = new();
	private readonly ZingApp _app;
	private readonly HostWindow _window;
	private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
	private readonly Bridge _bridge = new();
	// The overlays' stacking order, for every overlay of the window (the layers and the surface).
	private readonly OverlayHost _overlays = new();
	private readonly WindowLayout _layout;
	private readonly DownloadHost _downloads;
	private readonly PermissionHost _permissions;
	private readonly List<RendererView> _renderers = [];
	private RendererView? _chrome;
	private RendererView? _surface;
	private NoticeLayer? _notices;
	private PageUiLayer? _pageUi;
	private CoveringLayer? _stacks;
	private CoveringLayer? _overview;
	private CoreWebView2Environment? _environment;
	private WebView2Page? _shown;
	// A split on screen: its layout and its pages in pane order (_shown is the focused one). Empty otherwise.
	private SplitLayout? _split;
	private List<WebView2Page> _panes = [];
	private DispatcherScheduler? _scheduler;
	private Rect? _surfaceRegion;
	private Task? _surfaceReady;
	private bool _surfaceWantsFocus;
	private bool _fullScreen;
	private Microsoft.UI.Windowing.AppWindowPresenter? _windowed;

	public MainWindow(ZingApp app)
	{
		_app = app;
		_downloads = new DownloadHost(() => Api);
		_permissions = new PermissionHost(() => Api);
		_window = new HostWindow("Zing", 1280, 820);
		_layout = new WindowLayout(_window, () => Api);
		if (TestHooks.KeepsOutOfTheWay)
		{
			AppWindow.Move(new Windows.Graphics.PointInt32(-20000, -20000));
			AppWindow.IsShownInSwitchers = false;
		}
		_window.Resized += Relayout;
		// Keys from every view of the window, web pages included, before the page sees them.
		_window.KeyPressed += OnKey;
		_window.Destroyed += OnClosed;
		_window.Activated += () => _app.Focused(this);
		_window.DefaultFocus = () => _shown?.View ?? _chrome?.View;
		TestHooks.Watch(_scope, () => Faults.Enqueue(_dispatcher, _window.Close, "test close"));
		TestHooks.Register(_window, () => Api);
		TestHooks.RegisterPickerKeys(_window, PickerKeyOnPage);
		_app.Services.Vaults.Changed += FollowAutofill;
	}

	public Microsoft.UI.Windowing.AppWindow AppWindow => _window.AppWindow;

	/// <summary>The window's handle, which owns a Windows Hello prompt (HelloSealer).</summary>
	public nint Hwnd => _window.Hwnd;

	/// <summary>Shows the window; test runs never take focus (AGENTS.md).</summary>
	public void Show() => _window.Show(activate: !TestHooks.KeepsOutOfTheWay);

	public ChromeApi? Api { get; private set; }

	/// <summary>
	/// Called once the app has listed the window, so nothing it does on starting finds it missing. A window
	/// closed while it started is gone, not broken (ViewGoneException); any other failure is recorded.
	/// </summary>
	public void Start() => TestHooks.Background(StartAsync(), "startup");

	public IPageHost Pages => this;

	/// <summary>A started page of this window, for profile-wide work such as purging a site.</summary>
	public CoreWebView2? AnyLivePage() =>
		Api?.Tabs.Records.Select(r => Api.Tabs.PageOf(r.Id)).OfType<WebView2Page>().FirstOrDefault(p => p.IsOpen && p.IsStarted)?.Core;

	// Construction order (AGENTS.md): every field exists before anything can call back into the window.
	// After each await the window may have closed: the scope's checkpoint stops here then (ViewGoneException).
	private async Task StartAsync()
	{
		_scope.Checkpoint("startup");
		_environment = await _app.Environment;
		_scope.Checkpoint("startup");
		_window.Theme = Theme();
		// The chrome view is created first, under every page; its API must be registered before it loads and asks for state.
		var chrome = RendererView.CreateAsync(_window, _scope, _environment, _bridge, "chrome", overlay: false);
		_notices = new NoticeLayer(_window, _scope, _environment, _bridge, _layout.ContentRect, _dispatcher, _overlays);
		_notices.OfferEnded += () => OfferEnded?.Invoke();
		_stacks = new CoveringLayer(_window, _scope, _environment, _bridge, "overlay-stack", _layout.ContentRect, () => Zing.Core.Layout.TabStripHeight, _renderers.Add, _overlays, OverlayLevel.StackDropdown);
		_overview = new CoveringLayer(_window, _scope, _environment, _bridge, "overlay-overview", _layout.ContentRect, () => Api?.Insets().Top ?? 0, _renderers.Add, _overlays, OverlayLevel.Overview);
		_pageUi = new PageUiLayer(_window, _scope, _environment, _bridge, _layout.ContentRect, () => Api?.Insets().Top ?? 0, _renderers.Add, _overlays);
		_bridge.Register("default-browser:open-settings", _ =>
		{
			Shell.OpenExternal("ms-settings:defaultapps");
			return (object?)null;
		});
		// A Zing view's own error (RendererErrorReport): recorded like the host's, so an e2e run fails on it.
		_bridge.Register("app:report-error", (JsonElement[] args) =>
		{
			TestHooks.Record("renderer", args[0].GetRawText());
			return (object?)null;
		});
		_bridge.Register("chrome:titlebar-colors", (JsonElement[] args) =>
		{
			var colors = args[0];
			var isSet = colors.ValueKind != JsonValueKind.Null;
			Windows.UI.Color? Colour(string name) => isSet ? ParseColor(colors.GetProperty(name).GetString()!) : null;
			var ink = Colour("symbolColor");
			var background = Colour("background");
			var bar = AppWindow.TitleBar;
			bar.ButtonForegroundColor = ink;
			bar.ButtonInactiveForegroundColor = ink;
			bar.ButtonHoverForegroundColor = ink;
			bar.ButtonPressedForegroundColor = ink;
			bar.ButtonBackgroundColor = background;
			bar.ButtonInactiveBackgroundColor = background;
			bar.ButtonHoverBackgroundColor = Colour("hover");
			bar.ButtonPressedBackgroundColor = Colour("pressed");
			return (object?)null;
		});
		Api = new ChromeApi(_bridge, _app.Services, this, typeof(MainWindow).Assembly.GetName().Version?.ToString() ?? "0");
		Api.TabsChanged += () => _app.TabsChanged(this);
		var themeSubscription = _app.Services.Settings.Subscribe((_, key) =>
		{
			if (key == "theme") _window.Theme = Theme();
		});
		_scope.Track(themeSubscription.Dispose);
		Api.Start(_app.TakePendingRestore(), System.Environment.GetEnvironmentVariable("ZING_START_URL"));
		_app.Ready(this);
		_chrome = await chrome;
		_scope.Checkpoint("startup");
		_renderers.Add(_chrome);
		// It fills the window, so every page goes over it, including pages started before it and after.
		_window.Backmost = _chrome.View;
		_chrome.Place(_layout.ContentRect());
		ReserveCaptionButtons();
		// A reloaded chrome (its renderer crashed) refetches its state, but the caption inset is a script the host sets.
		_chrome.Crashed += view => view.View.Core.NavigationCompleted += CaptionsAfterReload;
		TestHooks.Started(_scope);
	}

	/// <summary>
	/// The minimise/maximise/close buttons sit over the chrome's top-right corner (the title bar is
	/// extended into the content). Electron's titleBarOverlay told the renderer through
	/// env(titlebar-area-*); WebView2 has no such thing, so the host sets the strip's padding itself.
	/// </summary>
	private void ReserveCaptionButtons()
	{
		if (_chrome is null) return;
		var px = _layout.CaptionInset();
		TestHooks.Background(_chrome.View.Core.ExecuteScriptAsync($"document.documentElement.style.setProperty('--caption-inset', '{px}px')").AsTask(), "caption inset");
	}

	private void CaptionsAfterReload(CoreWebView2 core, CoreWebView2NavigationCompletedEventArgs e)
	{
		core.NavigationCompleted -= CaptionsAfterReload;
		ReserveCaptionButtons();
	}

	private string Theme() => _app.Services.Settings.Get("theme")!.GetValue<string>();

	// MARK: IPageHost

	private static RelayedSite RelayedSiteFor(AppServices services) =>
		new((services.Net ?? NetServices.Live).RelayedPageTransport, () => services.Settings.Get("dohProvider")?.GetValue<string>() ?? throw new InvalidDataException("Setting dohProvider is missing"), services.Consent);

	public IPage Create(IPageEvents events)
	{
		var settings = _app.Services.Settings;
		// Created after the chrome view, so above it; overlays are popups above both.
		return new WebView2Page(_window, _scope, events, _environment ?? throw new InvalidOperationException("No WebView2 environment yet"), url => _app.Services.Threats?.Serve(url, settings.Snapshot()) ?? StartPage.Handle(url, settings.Snapshot()), this, _app.PersistentProfile, RelayedSiteFor(_app.Services));
	}

	public void Show(IPage page)
	{
		var shown = (WebView2Page)page;
		if (_shown is not null && _shown != shown)
		{
			// A tab left in fullscreen would still own the screen the next time it shows.
			_shown.ExitFullScreen();
			_shown.Visible = false;
		}
		_shown = shown;
		_split = null;
		_panes = [];
		_window.ForgetFocusedPage(IsPageView, shown.View);
		PlaceShown();
		shown.Visible = true;
		// Over any page that is still drawn while its picture is taken (Hide).
		shown.BringToFront();
		FollowFullScreen();
	}

	/// <summary>Longest a leaving page stays drawn under the next one for its overview picture (TabOverview's capture timeout).</summary>
	private static readonly TimeSpan LeavingPictureWait = TimeSpan.FromMilliseconds(1500);

	/// <summary>A tab's page, as opposed to the chrome or an overlay.</summary>
	private bool IsPageView(HostedView view) => view != _chrome?.View && !view.IsOverlay;

	/// <summary>A split's pages side by side; the tab service hides whatever is no longer shown.</summary>
	public void ShowSplit(IReadOnlyList<IPage> pages, SplitLayout layout, IPage focused)
	{
		var panes = pages.Cast<WebView2Page>().ToList();
		// The page that had the screen goes out of fullscreen when another pane takes the focus.
		if (_shown is not null && _shown != focused) _shown.ExitFullScreen();
		_shown = (WebView2Page)focused;
		_split = layout;
		_panes = panes;
		// A pane that had the keyboard before another became active mustn't take it back (and with it the active tab).
		_window.ForgetFocusedPage(IsPageView, _shown.View);
		PlaceShown();
		foreach (var pane in panes)
		{
			pane.Visible = true;
			pane.BringToFront();
		}
		FollowFullScreen();
	}

	public void Hide(IPage page)
	{
		var hidden = (WebView2Page)page;
		_panes.Remove(hidden);
		if (_shown == hidden)
		{
			_shown = null;
			FollowFullScreen();
		}
		// A page leaving the screen is being pictured for the overview (TabOverview.Leaving). A hidden WebView2 never
		// finishes a capture: the call stays pending in the engine, the tab gets no picture, and if that page's
		// renderer later dies the engine's browser process crashes (0xC0000005). So it stays drawn, under the
		// page that replaces it (Show raises that one), until the picture is taken.
		if (hidden.PendingCapture is { IsCompleted: false } capture && hidden.IsOpen)
		{
			TestHooks.Background(HideAfterPictureAsync(hidden, capture), "hide page");
			return;
		}
		hidden.Visible = false;
	}

	private async Task HideAfterPictureAsync(WebView2Page page, Task capture)
	{
		await Task.WhenAny(capture, Task.Delay(LeavingPictureWait));
		// Shown again meanwhile (in front, or as a pane of a split), or gone with its tab or window: nothing to hide.
		if (_scope.IsClosed || !page.IsOpen || _shown == page || _panes.Contains(page)) return;
		page.Visible = false;
	}

	// MARK: IWindowHost

	public void Push(string channel, object? payload)
	{
		var json = Bridge.Event(channel, payload);
		void Send()
		{
			foreach (var renderer in _renderers) renderer.Send(json);
		}
		if (_dispatcher.HasThreadAccess) Send();
		else Faults.Enqueue(_dispatcher, Send, $"event {channel}");
	}

	/// <summary>Any thread may report something; the notice is drawn on the window's own.</summary>
	public void Notify(string text)
	{
		if (!_dispatcher.HasThreadAccess)
		{
			Faults.Enqueue(_dispatcher, () => Notify(text), "notice");
			return;
		}
		if (_notices is null || _scope.IsClosed) return;
		TestHooks.Background(_notices.ShowAsync(text), "notice");
	}

	public event Action? OfferEnded;

	public void OfferUnblock(string address)
	{
		if (!_dispatcher.HasThreadAccess)
		{
			Faults.Enqueue(_dispatcher, () => OfferUnblock(address), "offer");
			return;
		}
		if (_notices is null || _scope.IsClosed) return;
		TestHooks.Background(_notices.ShowOfferAsync(address), "offer");
	}

	public void DismissOffer()
	{
		if (!_dispatcher.HasThreadAccess)
		{
			Faults.Enqueue(_dispatcher, DismissOffer, "offer");
			return;
		}
		_notices?.Hide();
	}

	public void NewWindow() => _app.NewWindow();

	// Not inside the bridge call that asked: the window takes its renderer views with it.
	public void CloseWindow() => _dispatcher.TryEnqueue(_window.Close);

	public void Relayout()
	{
		// The buttons' width changes with the DPI and between maximised and restored.
		ReserveCaptionButtons();
		_chrome?.Place(_layout.ContentRect());
		PlaceShown();
		_notices?.Layout();
		_pageUi?.Layout();
		_stacks?.Layout();
		_overview?.Layout();
		// The shell model works out where the surface goes and answers through SurfaceRegionChanged.
		Api?.LayoutChanged();
	}

	public ContentSize ContentSize => _layout.ContentSize;

	public Point ContentOrigin => _layout.ContentOrigin;

	/// <summary>Nothing reveals itself over a fullscreen page: the cursor counts as away.</summary>
	public Point? Cursor => _fullScreen ? null : _layout.Cursor;

	public void SurfaceRegionChanged(Rect? region)
	{
		_surfaceRegion = region;
		if (region is null)
		{
			if (_surface is not null) _surface.Visible = false;
			return;
		}
		TestHooks.Background(ShowSurfaceAsync(), "surface");
	}

	/// <summary>A link from another app (windows.ts openLink).</summary>
	public void OpenLink(string url) => (Api ?? throw new InvalidOperationException("The window has not started")).OpenLink(url);

	/// <summary>Brings the window forward; test runs never take focus (AGENTS.md).</summary>
	public void Reveal()
	{
		_window.Restore();
		if (!TestHooks.KeepsOutOfTheWay) _window.Show(activate: true);
	}

	public void FocusChrome() => _chrome?.View.Focus();

	/// <summary>Focuses the surface once it shows: it is asked as it opens, and a hidden window can't take the keyboard.</summary>
	public void FocusSurface()
	{
		if (_surface is { Visible: true } surface)
		{
			surface.View.Focus();
			return;
		}
		_surfaceWantsFocus = true;
	}

	public void PageUiChanged(PageUiState state) => _pageUi?.Changed(state);

	public string DownloadsDirectory => _downloads.Directory;

	public IDownloadHost DownloadHost => _downloads.Files;

	public async Task<IReadOnlyList<string>> PickFilesAsync(IReadOnlyList<string> extensions)
	{
		if (TestHooks.IsEndToEndRun) return TestHooks.TakePickedFiles();
		var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(_window.AppWindow.Id);
		if (extensions.Count == 0) picker.FileTypeFilter.Add("*");
		foreach (var extension in extensions) picker.FileTypeFilter.Add("." + extension);
		var picked = await picker.PickMultipleFilesAsync();
		return [.. picked.Select(file => file.Path)];
	}

	private SecretClipboard? _secretClipboard;

	public Zing.Core.Vault.ISecretClipboard SecretClipboard => _secretClipboard ??= new SecretClipboard(_window.Hwnd);

	public void CopyFilesToClipboard(IReadOnlyList<string> paths)
	{
		if (!_window.OnUiThread) throw new InvalidOperationException("Files can only be put on the clipboard from the window's own thread");
		FileClipboard.Copy(_window.Hwnd, paths);
	}

	public Task PrintHtmlToPdfAsync(string html, string pdfPath, CancellationToken cancellation) => HtmlPdfPrinter.PrintAsync(_window, _scope, html, pdfPath, cancellation);

	// MARK: IPageFeatureHost

	public void DownloadStarting(WebView2Page page, CoreWebView2DownloadStartingEventArgs e) => _downloads.Starting(page, e);

	public void PermissionRequested(WebView2Page page, CoreWebView2PermissionRequestedEventArgs e) => _permissions.Requested(page, e);

	public void Left(WebView2Page page) => _permissions.Left(page);

	public void FullScreenChanged(WebView2Page page)
	{
		// Only the shown page may have the screen; one in the background (a video that went fullscreen as its tab left) is put back.
		if (page != _shown)
		{
			page.ExitFullScreen();
			return;
		}
		FollowFullScreen();
	}

	/// <summary>
	/// The window follows the shown page: fullscreen while one of its elements is (the page then covers the chrome),
	/// back to a normal window when it leaves. Test windows stay off-screen, so they only change the layout (AGENTS.md).
	/// </summary>
	private void FollowFullScreen()
	{
		var wanted = _shown?.IsFullScreen == true;
		if (wanted == _fullScreen) return;
		_fullScreen = wanted;
		if (!TestHooks.KeepsOutOfTheWay)
		{
			// The window's own presenter goes back as it was, maximised or not.
			if (wanted)
			{
				_windowed = AppWindow.Presenter;
				AppWindow.SetPresenter(Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen);
			}
			else AppWindow.SetPresenter(_windowed ?? throw new InvalidOperationException("Left fullscreen without having entered it"));
		}
		Relayout();
	}

	/// <summary>The shown page goes inside the chrome, or over the whole window while it is fullscreen.</summary>
	private void PlaceShown()
	{
		if (_shown is null) return;
		if (_fullScreen)
		{
			_shown.Place(_layout.ContentRect());
			_shown.BringToFront();
			return;
		}
		if (_split is null || _panes.Count != _split.TabIds.Count)
		{
			_layout.PlacePage(_shown);
			return;
		}
		if (_layout.PageRect() is not { } area) return;
		var rects = Split.PaneRects(area, _split);
		for (var i = 0; i < _panes.Count; i++) _panes[i].Place(rects[i]);
	}

	public void SetPageUiHeight(int height) => _pageUi?.SetHeight(height);

	public void FocusPageUi() => _pageUi?.Focus();

	public void AccountPickerChanged(Zing.Core.Vault.AccountPicker? picker) => _pageUi?.PickerChanged(picker);

	public void SetAccountPickerHeight(int height) => _pageUi?.SetPickerHeight(height);

	public bool AutofillOn => _app.Services.Vaults.Current is not null;

	/// <summary>passwordAutofill was turned on or off: every open page's channel and script follow it (Ruling 30).</summary>
	private void FollowAutofill()
	{
		if (Api is not { } api) return;
		foreach (var page in api.Tabs.Records.Select(r => api.Tabs.PageOf(r.Id)).OfType<WebView2Page>().Where(p => p.IsOpen))
			TestHooks.Background(page.FollowAutofillAsync(), "autofill setting");
	}

	public void Report(string what, Exception error) => TestHooks.Record(what, error);

	public void StackDropdownChanged(StackDropdownState? state) => _stacks?.Changed(state is not null);

	public void OverviewChanged(OverviewState? state) => _overview?.Changed(state is not null);

	/// <summary>An optional feature failed, as it may: a warning line in the fault log, never ZING_ERRORS_FILE.</summary>
	public void Warn(string what, Exception error)
	{
		System.Diagnostics.Debug.WriteLine($"{what}: {error.Message}");
		Faults.Warning(what, error);
	}

	public void Background(Task task, string what) => TestHooks.Background(task, what);

	public IShellActions Shell { get; } = new WindowsShell();

	public IScheduler Scheduler => _scheduler ??= new DispatcherScheduler(_dispatcher);

	public void ShowMenu(IReadOnlyList<Zing.Core.MenuItem> items, double x, double y)
	{
		// A menu would show on the person's screen even for the off-screen test window (AGENTS.md).
		if (TestHooks.IsEndToEndRun)
		{
			TestHooks.RecordMenu(items);
			return;
		}
		// Not inside the bridge call that asked: a menu runs its own message loop until it closes.
		_dispatcher.TryEnqueue(() => NativeMenus.Show(_window, items, x, y));
	}

	/// <summary>Zing's shortcuts win over the page's and the engine's own (Ctrl+F is Zing's find, not Edge's).</summary>
	private void OnKey(HostedView view, CoreWebView2AcceleratorKeyPressedEventArgs e)
	{
		if (e.KeyEventKind is not (CoreWebView2KeyEventKind.KeyDown or CoreWebView2KeyEventKind.SystemKeyDown)) return;
		if (PickerKey(view, (int)e.VirtualKey))
		{
			e.Handled = true;
			return;
		}
		if (Api is null || Commands.CodeOf((int)e.VirtualKey) is not { } code) return;
		const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12;
		var input = new KeyInput(code, Native.IsDown(VK_CONTROL), Native.IsDown(VK_SHIFT), Native.IsDown(VK_MENU));
		if (Api.HandleKey(input)) e.Handled = true;
	}

	/// <summary>
	/// The account picker's keys (Ruling 28): real key presses while it's open, on its page's view or the picker's own,
	/// never anything a page posts. True when the picker took the key, so the page mustn't get it.
	/// </summary>
	private bool PickerKey(HostedView view, int virtualKey)
	{
		if (Api?.Autofill.Picker is not { } picker || Zing.Core.Vault.PickerKeys.FromVirtualKey(virtualKey) is not { } key) return false;
		var onPage = Api.Tabs.PageOf(picker.PageId) is WebView2Page page && ReferenceEquals(page.View, view);
		if (!onPage && _pageUi?.HostsPicker(view) != true) return false;
		return Api.Autofill.Key(key);
	}

	/// <summary>The e2e seam (picker-key): CDP input never reaches AcceleratorKeyPressed, so a test enters PickerKey as the picker's page would.</summary>
	private bool PickerKeyOnPage(int virtualKey) =>
		Api?.Autofill.Picker is { } picker && Api.Tabs.PageOf(picker.PageId) is WebView2Page { View: { } view } && PickerKey(view, virtualKey);

	// MARK: Surface

	/// <summary>The surface floats above the page and chrome, sized to what it shows, so it never takes the page's clicks.</summary>
	private async Task ShowSurfaceAsync()
	{
		// One view, however many layout passes ask for it while it loads: a second one was left on
		// top when the first was hidden, and covered the window after "Skip for now".
		await (_surfaceReady ??= CreateSurfaceAsync());
		_scope.Checkpoint("surface");
		// The region may have gone (gate passed, sheet closed) while the view loaded.
		if (_surface is null) return;
		if (_surfaceRegion is not { } region)
		{
			_surface.Visible = false;
			return;
		}
		_surface.Place(region);
		_overlays.Show(OverlayLevel.Surface);
		_surface.Visible = true;
		if (!_surfaceWantsFocus) return;
		_surfaceWantsFocus = false;
		_surface.View.Focus();
	}

	private async Task CreateSurfaceAsync()
	{
		var surface = await RendererView.CreateAsync(_window, _scope, _environment!, _bridge, "surface", overlay: true);
		_scope.ThrowIfClosed("surface");
		_surface = surface;
		_overlays.Add(OverlayLevel.Surface, surface.View);
		_renderers.Add(_surface);
		// It may have missed the state pushed while it loaded.
		if (Api is not null) _surface.Send(Bridge.Event("shell:state", Api.Shell.State()));
	}

	private void OnClosed()
	{
		// The tabs first (Tabs.Destroy closes their pages while they are still open), then everything else
		// the window started: renderer views, those still loading included, and the theme subscription.
		_app.Services.Vaults.Changed -= FollowAutofill;
		Api?.Dispose();
		_scope.Close();
		_notices?.Destroy();
		_app.Closed(this);
	}

	private static Windows.UI.Color ParseColor(string hex)
	{
		if (hex.Length != 7 || hex[0] != '#') throw new FormatException($"Expected #RRGGBB, got {hex}");
		return Windows.UI.Color.FromArgb(255, Convert.ToByte(hex[1..3], 16), Convert.ToByte(hex[3..5], 16), Convert.ToByte(hex[5..7], 16));
	}
}
