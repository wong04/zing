using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Zing.Core;

namespace Zing.Host;

/// <summary>
/// One browser window (zing-window.ts): the chrome renderer at the bottom, the
/// active tab's page above it, then the settings surface and the notice layer.
/// Later children of the root grid paint above earlier ones.
/// </summary>
public sealed class MainWindow : Window, IWindowHost, IPageHost, IPageFeatureHost
{
	private readonly ZingApp _app;
	private readonly Grid _root = new();
	private readonly Bridge _bridge = new();
	private readonly List<RendererView> _renderers = [];
	private RendererView? _chrome;
	private RendererView? _surface;
	private NoticeLayer? _notices;
	private PageUiLayer? _pageUi;
	private StackLayer? _stacks;
	private readonly WindowsDownloads _downloads = new();
	private CoreWebView2Environment? _environment;
	private WebView2Page? _shown;
	private bool _closed;
	private DispatcherScheduler? _scheduler;
	private Rect? _surfaceRegion;
	private IDisposable? _themeSubscription;

	public MainWindow(ZingApp app)
	{
		_app = app;
		Title = "Zing";
		SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
		ExtendsContentIntoTitleBar = true;
		Content = _root;
		Place();
		_root.SizeChanged += (_, _) => Relayout();
		// Keys from every view of the window, web pages included: WebView2 raises accelerator keys as KeyDown.
		_root.AddHandler(UIElement.KeyDownEvent, new Microsoft.UI.Xaml.Input.KeyEventHandler(OnKeyDown), handledEventsToo: false);
		Closed += (_, _) => OnClosed();
		Activated += (_, e) =>
		{
			if (e.WindowActivationState != WindowActivationState.Deactivated) _app.Focused(this);
		};
	}

	public ChromeApi? Api { get; private set; }

	/// <summary>Called once the app has listed the window, so nothing it does on starting finds it missing.</summary>
	public void Start() => _ = StartAsync().ContinueWith(t => TestHooks.Record("startup", t.Exception!), TaskContinuationOptions.OnlyOnFaulted);

	public IPageHost Pages => this;

	/// <summary>A started page of this window, for profile-wide work such as purging a site.</summary>
	public CoreWebView2? AnyLivePage() =>
		Api?.Tabs.Records.Select(r => Api.Tabs.PageOf(r.Id)).OfType<WebView2Page>().FirstOrDefault(p => p.IsOpen && p.IsStarted)?.Core;

	private void Place()
	{
		AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 820));
		if (!TestHooks.IsEndToEndRun) return;
		AppWindow.Move(new Windows.Graphics.PointInt32(-20000, -20000));
		AppWindow.IsShownInSwitchers = false;
	}

	// Construction order (AGENTS.md): every field exists before anything can call back into the window.
	private async Task StartAsync()
	{
		_environment = await _app.Environment;
		// The chrome view joins the grid at once, under every page; its API must be registered before it loads and asks for state.
		var chrome = RendererView.CreateAsync(_root, _environment, _bridge, "chrome", transparent: false);
		_notices = new NoticeLayer(_root, _environment, _bridge, ContentRect, DispatcherQueue);
		_stacks = new StackLayer(_root, _environment, _bridge, ContentRect, _renderers.Add);
		_pageUi = new PageUiLayer(_root, _environment, _bridge, ContentRect, () => Api?.Insets().Top ?? 0, _renderers.Add);
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
	}

	// MARK: IPageHost

	public IPage Create(IPageEvents events)
	{
		var view = new WebView2 { Visibility = Visibility.Collapsed };
		// Pages go just above the chrome renderer, under every layer.
		_root.Children.Insert(Math.Min(1, _root.Children.Count), view);
		var settings = _app.Services.Settings;
		return new WebView2Page(view, events, _environment ?? throw new InvalidOperationException("No WebView2 environment yet"), url => StartPage.Handle(url, settings.Snapshot()), () => settings.Get("theme")!.GetValue<string>(), this);
	}

	public void Show(IPage page)
	{
		var shown = (WebView2Page)page;
		if (_shown is not null && _shown != shown) _shown.View.Visibility = Visibility.Collapsed;
		_shown = shown;
		PlacePage(shown);
		shown.View.Visibility = Visibility.Visible;
	}

	public void Hide(IPage page)
	{
		var hidden = (WebView2Page)page;
		hidden.View.Visibility = Visibility.Collapsed;
		if (_shown == hidden) _shown = null;
		if (!hidden.IsOpen) _root.Children.Remove(hidden.View);
	}

	// MARK: IWindowHost

	public void Push(string channel, object? payload)
	{
		var json = Bridge.Event(channel, payload);
		void Send()
		{
			foreach (var renderer in _renderers) renderer.Send(json);
		}
		if (DispatcherQueue.HasThreadAccess) Send();
		else DispatcherQueue.TryEnqueue(Send);
	}

	/// <summary>Any thread may report something; the notice is drawn on the window's own.</summary>
	public void Notify(string text)
	{
		if (!DispatcherQueue.HasThreadAccess)
		{
			DispatcherQueue.TryEnqueue(() => Notify(text));
			return;
		}
		if (_notices is null || _closed) return;
		TestHooks.Background(_notices.ShowAsync(text), "notice");
	}

	public void NewWindow() => _app.NewWindow();

	// Not inside the bridge call that asked: the window takes its renderer views with it.
	public void CloseWindow() => DispatcherQueue.TryEnqueue(Close);

	public void Relayout()
	{
		if (_shown is not null) PlacePage(_shown);
		_notices?.Layout();
		_pageUi?.Layout();
		_stacks?.Layout();
		// The shell model works out where the surface goes and answers through SurfaceRegionChanged.
		Api?.LayoutChanged();
	}

	public ContentSize ContentSize => new(_root.ActualWidth, _root.ActualHeight);

	/// <summary>The content's top-left on screen in DIPs, the units of the renderer's screen points.</summary>
	public Point ContentOrigin
	{
		get
		{
			var origin = new Native.POINT();
			Native.ClientToScreen(WinRT.Interop.WindowNative.GetWindowHandle(this), ref origin);
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
			var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
			if (Native.GetForegroundWindow() != hwnd || !Native.GetCursorPos(out var cursor)) return null;
			var origin = ContentOrigin;
			var scale = Scale;
			var point = new Point(cursor.X / scale - origin.X, cursor.Y / scale - origin.Y);
			var size = ContentSize;
			return point.X < 0 || point.Y < 0 || point.X > size.Width || point.Y > size.Height ? null : point;
		}
	}

	private double Scale => _root.XamlRoot?.RasterizationScale ?? 1;

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
		if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter { State: Microsoft.UI.Windowing.OverlappedPresenterState.Minimized } presenter) presenter.Restore();
		if (!TestHooks.IsEndToEndRun) Activate();
	}

	public void FocusChrome() => _chrome?.View.Focus(FocusState.Programmatic);

	public void FocusSurface() => _surface?.View.Focus(FocusState.Programmatic);

	public void PageUiChanged(PageUiState state) => _pageUi?.Changed(state);

	public string DownloadsDirectory { get; } = WindowsDownloads.Directory();

	public IDownloadHost DownloadHost => _downloads;

	// MARK: IPageFeatureHost

	public void DownloadStarting(WebView2Page page, CoreWebView2DownloadStartingEventArgs e)
	{
		if (Api is null) return;
		var operation = e.DownloadOperation;
		var name = Path.GetFileName(e.ResultFilePath);
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

	public void StackDropdownChanged(StackDropdownState? state) => _stacks?.Changed(state);

	public IShellActions Shell { get; } = new WindowsShell();

	public IScheduler Scheduler => _scheduler ??= new DispatcherScheduler(DispatcherQueue);

	public void ShowMenu(IReadOnlyList<Zing.Core.MenuItem> items, double x, double y)
	{
		var flyout = new MenuFlyout();
		foreach (var item in items) flyout.Items.Add(NativeMenus.Build(item));
		flyout.ShowAt(_root, new Windows.Foundation.Point(x, y));
	}

	private void OnKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
	{
		if (Api is null || Commands.CodeOf((int)e.Key) is not { } code) return;
		static bool Down(Windows.System.VirtualKey key) =>
			Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
		var input = new KeyInput(code, Down(Windows.System.VirtualKey.Control), Down(Windows.System.VirtualKey.Shift), Down(Windows.System.VirtualKey.Menu));
		if (Api.HandleKey(input)) e.Handled = true;
	}

	// MARK: Layout

	private Rect ContentRect() => new(0, 0, _root.ActualWidth, _root.ActualHeight);

	private void PlacePage(WebView2Page page)
	{
		if (Api is null) return;
		var insets = Api.Insets();
		var content = ContentRect();
		page.View.HorizontalAlignment = HorizontalAlignment.Left;
		page.View.VerticalAlignment = VerticalAlignment.Top;
		page.View.Margin = new Thickness(insets.Left, insets.Top, 0, 0);
		page.View.Width = Math.Max(0, content.Width - insets.Left - insets.Right);
		page.View.Height = Math.Max(0, content.Height - insets.Top);
	}

	/// <summary>The settings sheet covers the whole window while it is open (panels.ts surfaceRegion).</summary>
	/// <summary>The surface floats above the page and chrome, sized to what it shows, so it never takes the page's clicks.</summary>
	private async Task ShowSurfaceAsync()
	{
		if (_surface is null)
		{
			_surface = await RendererView.CreateAsync(_root, _environment!, _bridge, "surface", transparent: true);
			_renderers.Add(_surface);
			// It may have missed the state pushed while it loaded.
			if (Api is not null) _surface.Send(Bridge.Event("shell:state", Api.Shell.State()));
		}
		if (_closed || _surfaceRegion is not { } region) return;
		_surface.Place(region);
		Layers.Raise(_root, _surface.View);
		_notices?.Raise();
		_surface.Visible = true;
	}

	private void OnClosed()
	{
		_closed = true;
		_themeSubscription?.Dispose();
		Api?.Dispose();
		_notices?.Destroy();
		_pageUi?.Destroy();
		_stacks?.Destroy();
		foreach (var renderer in _renderers) renderer.Close();
		_app.Closed(this);
	}

	private static Windows.UI.Color ParseColor(string hex)
	{
		if (hex.Length != 7 || hex[0] != '#') throw new FormatException($"Expected #RRGGBB, got {hex}");
		return Windows.UI.Color.FromArgb(255, Convert.ToByte(hex[1..3], 16), Convert.ToByte(hex[3..5], 16), Convert.ToByte(hex[5..7], 16));
	}
}
