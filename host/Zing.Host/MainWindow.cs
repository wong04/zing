using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Zing.Core;

namespace Zing.Host;

/// <summary>
/// One browser window (zing-window.ts): the chrome renderer filling the window, the
/// active tab's page over it, and above them the overlays, each in a popup of its own
/// (the settings surface, notices, page UI, the stack dropdown and the overview).
/// </summary>
public sealed class MainWindow : IWindowHost, IPageHost, IPageFeatureHost
{
	private readonly ZingApp _app;
	private readonly HostWindow _window;
	private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
	private readonly Bridge _bridge = new();
	private readonly List<RendererView> _renderers = [];
	private RendererView? _chrome;
	private RendererView? _surface;
	private NoticeLayer? _notices;
	private PageUiLayer? _pageUi;
	private CoveringLayer? _stacks;
	private CoveringLayer? _overview;
	private readonly WindowsDownloads _downloads = new();
	private CoreWebView2Environment? _environment;
	private WebView2Page? _shown;
	private bool _closed;
	private DispatcherScheduler? _scheduler;
	private Rect? _surfaceRegion;
	private Task? _surfaceReady;
	private bool _surfaceWantsFocus;
	private IDisposable? _themeSubscription;

	public MainWindow(ZingApp app)
	{
		_app = app;
		_window = new HostWindow("Zing", 1280, 820);
		if (TestHooks.IsEndToEndRun)
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
	}

	public Microsoft.UI.Windowing.AppWindow AppWindow => _window.AppWindow;

	/// <summary>Shows the window; test runs never take focus (AGENTS.md).</summary>
	public void Show() => _window.Show(activate: !TestHooks.IsEndToEndRun);

	public ChromeApi? Api { get; private set; }

	/// <summary>Called once the app has listed the window, so nothing it does on starting finds it missing.</summary>
	public void Start() => _ = StartAsync().ContinueWith(t => TestHooks.Record("startup", t.Exception!), TaskContinuationOptions.OnlyOnFaulted);

	public IPageHost Pages => this;

	/// <summary>A started page of this window, for profile-wide work such as purging a site.</summary>
	public CoreWebView2? AnyLivePage() =>
		Api?.Tabs.Records.Select(r => Api.Tabs.PageOf(r.Id)).OfType<WebView2Page>().FirstOrDefault(p => p.IsOpen && p.IsStarted)?.Core;

	// Construction order (AGENTS.md): every field exists before anything can call back into the window.
	private async Task StartAsync()
	{
		_environment = await _app.Environment;
		// The chrome view is created first, under every page; its API must be registered before it loads and asks for state.
		var chrome = RendererView.CreateAsync(_window, _environment, _bridge, "chrome", overlay: false);
		_notices = new NoticeLayer(_window, _environment, _bridge, ContentRect, _dispatcher);
		_stacks = new CoveringLayer(_window, _environment, _bridge, "overlay-stack", ContentRect, () => Zing.Core.Layout.TabStripHeight, _renderers.Add);
		_overview = new CoveringLayer(_window, _environment, _bridge, "overlay-overview", ContentRect, () => Api?.Insets().Top ?? 0, _renderers.Add);
		_pageUi = new PageUiLayer(_window, _environment, _bridge, ContentRect, () => Api?.Insets().Top ?? 0, _renderers.Add);
		_bridge.Register("default-browser:open-settings", _ =>
		{
			Shell.OpenExternal("ms-settings:defaultapps");
			return (object?)null;
		});
		_bridge.Register("chrome:titlebar-colors", (JsonElement[] args) =>
		{
			AppWindow.TitleBar.ButtonForegroundColor = ParseColor(args[0].GetProperty("symbolColor").GetString()!);
			return (object?)null;
		});
		Api = new ChromeApi(_bridge, _app.Services, this, typeof(MainWindow).Assembly.GetName().Version?.ToString() ?? "0");
		Api.TabsChanged += () => _app.TabsChanged(this);
		_themeSubscription = _app.Services.Settings.Subscribe((_, key) =>
		{
			if (key != "theme") return;
			foreach (var record in Api.Tabs.Records)
				if (Api.Tabs.PageOf(record.Id) is WebView2Page page) page.FollowTheme();
		});
		Api.Start(_app.TakePendingRestore(), System.Environment.GetEnvironmentVariable("ZING_START_URL"));
		_app.Ready(this);
		_chrome = await chrome;
		_renderers.Add(_chrome);
		// It fills the window, so every page goes over it, including pages started before it and after.
		_window.Backmost = _chrome.View;
		_chrome.Place(ContentRect());
		ReserveCaptionButtons();
	}

	/// <summary>
	/// The minimise/maximise/close buttons sit over the chrome's top-right corner (the title bar is
	/// extended into the content). Electron's titleBarOverlay told the renderer through
	/// env(titlebar-area-*); WebView2 has no such thing, so the host sets the strip's padding itself.
	/// </summary>
	private void ReserveCaptionButtons()
	{
		if (_chrome is null) return;
		var inset = AppWindow.TitleBar.RightInset / Scale;
		var px = Math.Ceiling(inset).ToString(System.Globalization.CultureInfo.InvariantCulture);
		_ = _chrome.View.Core.ExecuteScriptAsync($"document.documentElement.style.setProperty('--caption-inset', '{px}px')");
	}

	// MARK: IPageHost

	public IPage Create(IPageEvents events)
	{
		var settings = _app.Services.Settings;
		// Created after the chrome view, so above it; overlays are popups above both.
		return new WebView2Page(_window, events, _environment ?? throw new InvalidOperationException("No WebView2 environment yet"), url => StartPage.Handle(url, settings.Snapshot()), () => settings.Get("theme")!.GetValue<string>(), this, _app.PersistentProfile);
	}

	public void Show(IPage page)
	{
		var shown = (WebView2Page)page;
		if (_shown is not null && _shown != shown) _shown.Visible = false;
		_shown = shown;
		PlacePage(shown);
		shown.Visible = true;
	}

	public void Hide(IPage page)
	{
		var hidden = (WebView2Page)page;
		hidden.Visible = false;
		if (_shown == hidden) _shown = null;
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
		else _dispatcher.TryEnqueue(Send);
	}

	/// <summary>Any thread may report something; the notice is drawn on the window's own.</summary>
	public void Notify(string text)
	{
		if (!_dispatcher.HasThreadAccess)
		{
			_dispatcher.TryEnqueue(() => Notify(text));
			return;
		}
		if (_notices is null || _closed) return;
		TestHooks.Background(_notices.ShowAsync(text), "notice");
	}

	public void NewWindow() => _app.NewWindow();

	// Not inside the bridge call that asked: the window takes its renderer views with it.
	public void CloseWindow() => _dispatcher.TryEnqueue(_window.Close);

	public void Relayout()
	{
		// The buttons' width changes with the DPI and between maximised and restored.
		ReserveCaptionButtons();
		_chrome?.Place(ContentRect());
		if (_shown is not null) PlacePage(_shown);
		_notices?.Layout();
		_pageUi?.Layout();
		_stacks?.Layout();
		_overview?.Layout();
		// The shell model works out where the surface goes and answers through SurfaceRegionChanged.
		Api?.LayoutChanged();
	}

	public ContentSize ContentSize => _window.ClientSize;

	/// <summary>The content's top-left on screen in DIPs, the units of the renderer's screen points.</summary>
	public Point ContentOrigin
	{
		get
		{
			var origin = _window.ClientOrigin;
			var scale = Scale;
			return new Point(origin.X / scale, origin.Y / scale);
		}
	}

	/// <summary>Test runs never read the real mouse (AGENTS.md), so the sidebar never reveals itself there.</summary>
	public Point? Cursor
	{
		get
		{
			if (TestHooks.IsEndToEndRun) return null;
			if (Native.GetForegroundWindow() != _window.Hwnd || !Native.GetCursorPos(out var cursor)) return null;
			var origin = ContentOrigin;
			var scale = Scale;
			var point = new Point(cursor.X / scale - origin.X, cursor.Y / scale - origin.Y);
			var size = ContentSize;
			return point.X < 0 || point.Y < 0 || point.X > size.Width || point.Y > size.Height ? null : point;
		}
	}

	private double Scale => _window.Scale;

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
		if (!TestHooks.IsEndToEndRun) _window.Show(activate: true);
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

	public string DownloadsDirectory { get; } = WindowsDownloads.Directory();

	public IDownloadHost DownloadHost => _downloads;

	// MARK: IPageFeatureHost

	public void DownloadStarting(WebView2Page page, CoreWebView2DownloadStartingEventArgs e)
	{
		if (Api is null) return;
		var operation = e.DownloadOperation;
		var name = DownloadNames.Suggested(operation.ContentDisposition, operation.Uri);
		var started = Api.Downloads.Start(name, Math.Max(0L, (long)operation.TotalBytesToReceive));
		e.ResultFilePath = started.Path;
		// Zing's downloads list replaces Edge's download bubble.
		e.Handled = true;
		_downloads.Track(started.Id, operation);
		var lastPublish = DateTime.MinValue;
		operation.BytesReceivedChanged += (_, _) =>
		{
			Api.Downloads.Progress(started.Id, operation.BytesReceived, Math.Max(0L, (long)operation.TotalBytesToReceive));
			if (DateTime.UtcNow - lastPublish < TimeSpan.FromMilliseconds(250)) return;
			lastPublish = DateTime.UtcNow;
			Api.Downloads.Publish();
		};
		operation.StateChanged += (_, _) =>
		{
			var outcome = operation.State switch
			{
				CoreWebView2DownloadState.Completed => DownloadOutcome.Completed,
				CoreWebView2DownloadState.Interrupted when operation.InterruptReason == CoreWebView2DownloadInterruptReason.UserCanceled => DownloadOutcome.Cancelled,
				CoreWebView2DownloadState.Interrupted => DownloadOutcome.Interrupted,
				_ => (DownloadOutcome?)null,
			};
			if (outcome is not { } done) return;
			_downloads.Forget(started.Id);
			Api.Downloads.Done(started.Id, done, operation.BytesReceived);
			if (done == DownloadOutcome.Completed) _ = MarkAsync(started, operation.Uri, page.Url);
		};
	}

	/// <summary>
	/// Without this stream SmartScreen and Office never treat the file as downloaded. The engine
	/// stamps a generic about:internet mark; Zing's carries the real HostUrl and ReferrerUrl and is
	/// held against the engine's (holdMark.ts).
	/// </summary>
	private async Task MarkAsync(StartedDownload started, string hostUrl, string referrer)
	{
		try
		{
			// A download started from Zing's own start page has no web referrer.
			var ours = new ZoneMark(ZoneIdentifier.InternetZone, hostUrl, referrer.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? referrer : null);
			await MarkOfTheWeb.HoldAsync(new ZoneFile(started.Path), ours, windowMs: 10_000, pollMs: 100, ms => Task.Delay(ms));
		}
		catch (IOException error)
		{
			Api?.Downloads.MarkFailed(started.Id, error.Message);
		}
	}

	public void PermissionRequested(WebView2Page page, CoreWebView2PermissionRequestedEventArgs e)
	{
		if (Api is null) return;
		var (permission, details) = PermissionDeferral.Describe(e);
		Api.Permissions.Request(page, page.Url, permission, details, new PermissionDeferral(e, e.GetDeferral()));
	}

	public void Left(WebView2Page page) => Api?.Permissions.DropFor(page);

	public void SetPageUiHeight(int height) => _pageUi?.SetHeight(height);

	public void FocusPageUi() => _pageUi?.Focus();

	public void Report(string what, Exception error) => TestHooks.Record(what, error);

	public void StackDropdownChanged(StackDropdownState? state) => _stacks?.Changed(state is not null);

	public void OverviewChanged(OverviewState? state) => _overview?.Changed(state is not null);

	public void Warn(string what, Exception error) => System.Diagnostics.Debug.WriteLine($"{what}: {error.Message}");

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
		if (Api is null || Commands.CodeOf((int)e.VirtualKey) is not { } code) return;
		const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12;
		var input = new KeyInput(code, Native.IsDown(VK_CONTROL), Native.IsDown(VK_SHIFT), Native.IsDown(VK_MENU));
		if (Api.HandleKey(input)) e.Handled = true;
	}

	// MARK: Layout

	private Rect ContentRect()
	{
		var size = _window.ClientSize;
		return new(0, 0, size.Width, size.Height);
	}

	private void PlacePage(WebView2Page page)
	{
		if (Api is null) return;
		var insets = Api.Insets();
		var content = ContentRect();
		page.Place(new Rect(insets.Left, insets.Top, Math.Max(0, content.Width - insets.Left - insets.Right), Math.Max(0, content.Height - insets.Top)));
	}

	/// <summary>The surface floats above the page and chrome, sized to what it shows, so it never takes the page's clicks.</summary>
	private async Task ShowSurfaceAsync()
	{
		// One view, however many layout passes ask for it while it loads: a second one was left on
		// top when the first was hidden, and covered the window after "Skip for now".
		await (_surfaceReady ??= CreateSurfaceAsync());
		// The region may have gone (gate passed, sheet closed) while the view loaded.
		if (_closed || _surface is null) return;
		if (_surfaceRegion is not { } region)
		{
			_surface.Visible = false;
			return;
		}
		_surface.Place(region);
		_surface.View.Raise();
		_notices?.Raise();
		_surface.Visible = true;
		if (!_surfaceWantsFocus) return;
		_surfaceWantsFocus = false;
		_surface.View.Focus();
	}

	private async Task CreateSurfaceAsync()
	{
		_surface = await RendererView.CreateAsync(_window, _environment!, _bridge, "surface", overlay: true);
		_renderers.Add(_surface);
		// It may have missed the state pushed while it loaded.
		if (Api is not null) _surface.Send(Bridge.Event("shell:state", Api.Shell.State()));
	}

	private void OnClosed()
	{
		_closed = true;
		_themeSubscription?.Dispose();
		Api?.Dispose();
		_notices?.Destroy();
		_pageUi?.Destroy();
		_stacks?.Destroy();
		_overview?.Destroy();
		foreach (var renderer in _renderers) renderer.Close();
		_app.Closed(this);
	}

	private static Windows.UI.Color ParseColor(string hex)
	{
		if (hex.Length != 7 || hex[0] != '#') throw new FormatException($"Expected #RRGGBB, got {hex}");
		return Windows.UI.Color.FromArgb(255, Convert.ToByte(hex[1..3], 16), Convert.ToByte(hex[3..5], 16), Convert.ToByte(hex[5..7], 16));
	}
}
