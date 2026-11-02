using Microsoft.Web.WebView2.Core;
using Zing.Core;

namespace Zing.Host;

/// <summary>
/// A tab's page over a WebView2. The control starts asynchronously, so calls
/// made before it is ready are queued and run in order once it is.
/// </summary>
public sealed class WebView2Page : IPage
{

	private readonly IPageEvents _events;
	private readonly Func<string, StartPageResponse> _serveStartPage;
	private readonly Func<string> _theme;
	private readonly IPageFeatureHost _features;
	private readonly bool _persistent;
	private readonly List<Action<CoreWebView2>> _queued = [];
	private readonly HostWindow _window;
	private readonly WindowScope _scope;
	private HostedView? _view;
	private Rect _bounds;
	private bool _visible;
	private CoreWebView2? _core;
	private string? _findTerm;
	private int _textSize = Zing.Core.TextSize.Default;
	private double _zoom = Zing.Core.Zoom.Default;
	private int _downloads;
	private ulong _navigation;
	private static readonly TimeSpan DownloadGrace = TimeSpan.FromSeconds(1);

	private static readonly Lazy<(string Script, string Css)> Reader = new(() =>
	{
		var dir = Path.Combine(AppContext.BaseDirectory, "Assets");
		return (File.ReadAllText(Path.Combine(dir, "reader.js")), File.ReadAllText(Path.Combine(dir, "reader.css")));
	});

	public WebView2Page(HostWindow window, WindowScope scope, IPageEvents events, CoreWebView2Environment environment, Func<string, StartPageResponse> serveStartPage, Func<string> theme, IPageFeatureHost features, bool persistent)
	{
		_window = window;
		_scope = scope;
		_events = events;
		_serveStartPage = serveStartPage;
		_theme = theme;
		_features = features;
		_persistent = persistent;
		Ready = StartAsync(environment);
		// A page whose window closed while it started is gone, not broken.
		TestHooks.Background(Ready, "page");
	}

	/// <summary>The page's view once the engine has started it.</summary>
	public HostedView? View => _view;

	/// <summary>Where the page is in the window's content, in DIPs; kept for when the view exists.</summary>
	public void Place(Rect bounds)
	{
		_bounds = bounds;
		_view?.Place(bounds);
	}

	/// <summary>Puts this page above the window's other pages (they are sibling windows); overlays are popups above all of them.</summary>
	public void BringToFront() => _view?.Raise();

	/// <summary>The engine capture under way, if any. A hidden WebView2 never finishes one, so a page leaving the screen stays drawn until it has.</summary>
	public Task? PendingCapture { get; private set; }

	public bool Visible
	{
		get => _visible;
		set
		{
			_visible = value;
			if (_view is not null) _view.Visible = value;
		}
	}

	/// <summary>Completes once the engine has a CoreWebView2 for this page; a popup hands it to the opener's request.</summary>
	public Task Ready { get; }

	public CoreWebView2 Core => _core ?? throw new InvalidOperationException("The page has not started");

	public bool IsOpen { get; private set; } = true;

	public bool IsStarted => _core is not null;

	public string Url => _core?.Source ?? "";
	public string Title => _core?.DocumentTitle ?? "";
	public bool IsLoading { get; private set; }
	public bool CanGoBack => _core?.CanGoBack ?? false;
	public bool CanGoForward => _core?.CanGoForward ?? false;

	public void Navigate(string url) => Run(core => core.Navigate(url));
	public void GoBack() => Run(core => core.GoBack());
	public void GoForward() => Run(core => core.GoForward());
	public void Reload() => Run(core => core.Reload());
	public void Stop() => Run(core => core.Stop());
	public void Focus() => Run(_ => _view!.Focus());
	public void Run(string script) => Run(core => Background(core.ExecuteScriptAsync(script).AsTask(), "script"));

	public void Find(string query, FindDirection direction, FindMode mode) => Run(core =>
	{
		if (mode == FindMode.Restart || _findTerm != query)
		{
			_findTerm = query;
			var options = core.Environment.CreateFindOptions();
			options.FindTerm = query;
			options.ShouldHighlightAllMatches = true;
			// Zing draws its own find bar.
			options.SuppressDefaultFindDialog = true;
			Background(core.Find.StartAsync(options).AsTask(), "find");
			return;
		}
		if (direction == FindDirection.Next) core.Find.FindNext();
		else core.Find.FindPrevious();
	});

	public int TextSize
	{
		get => _textSize;
		set
		{
			_textSize = value;
			Run(core => Background(core.ExecuteScriptAsync(Zing.Core.TextSize.Script(value)).AsTask(), "text size"));
		}
	}

	/// <summary>The controller's zoom factor, which the engine keeps across the page's navigations; set before the view exists, it applies once it does.</summary>
	public double Zoom
	{
		get => _view?.Controller.ZoomFactor ?? _zoom;
		set
		{
			_zoom = value;
			Run(_ => _view!.Controller.ZoomFactor = value);
		}
	}

	/// <summary>
	/// Engine captures not yet finished, in every window (TestHooks "live-counts"). The WebView2 runtime's
	/// browser process crashes (0xC0000005) when a page's renderer dies while a capture of it is pending, and a
	/// hidden page never finishes one; MainWindow.Hide keeps a leaving page drawn until it has. e2e checks it drains.
	/// </summary>
	public static int CapturesInFlight { get; private set; }

	public Task<string?> CaptureAsync()
	{
		var capture = CaptureNowAsync();
		PendingCapture = capture;
		return capture;
	}

	private async Task<string?> CaptureNowAsync()
	{
		if (!IsOpen || _core is null) return null;
		using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
		CapturesInFlight++;
		try
		{
			await WhileOpenAsync(_core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Jpeg, stream).AsTask());
		}
		catch (ViewGoneException)
		{
			// The tab closed while the engine drew it: no picture, as for a page that never started.
			return null;
		}
		finally
		{
			CapturesInFlight--;
		}
		if (stream.Size == 0) return null;
		using var memory = new MemoryStream();
		stream.Seek(0);
		await stream.AsStreamForRead().CopyToAsync(memory);
		return "data:image/jpeg;base64," + Convert.ToBase64String(memory.ToArray());
	}

	/// <summary>Edge's own print preview, inside the page; an e2e run records it instead.</summary>
	public void Print() => Run(core =>
	{
		if (TestHooks.IsEndToEndRun) TestHooks.RecordDialog("print", core.Source);
		else core.ShowPrintUI(CoreWebView2PrintDialogKind.Browser);
	});

	public bool InReader { get; private set; }

	/// <summary>Whether an element of the page is fullscreen; the window then gives the page all of itself.</summary>
	public bool IsFullScreen => _core?.ContainsFullScreenElement ?? false;

	/// <summary>Takes the page's element out of fullscreen, as the page's own exit would.</summary>
	public void ExitFullScreen()
	{
		if (IsFullScreen) Run(core => Background(core.ExecuteScriptAsync("document.exitFullscreen()").AsTask(), "exit fullscreen"));
	}

	/// <summary>
	/// Runs the reader bundle in an isolated world of the page (as Electron did), so page
	/// scripts can neither see nor tamper with it; the DOM it swaps in is the page's own.
	/// </summary>
	public async Task<string?> ShowReaderAsync()
	{
		if (!IsOpen || _core is null) return "Reader isn't available for this page";
		var (script, css) = Reader.Value;
		var core = _core;
		// The tab may close at any await; then the reader has nothing to show (ViewGoneException, which callers expect).
		using var tree = System.Text.Json.JsonDocument.Parse(await WhileOpenAsync(core.CallDevToolsProtocolMethodAsync("Page.getFrameTree", "{}").AsTask()));
		var frameId = tree.RootElement.GetProperty("frameTree").GetProperty("frame").GetProperty("id").GetString();
		using var world = System.Text.Json.JsonDocument.Parse(await WhileOpenAsync(core.CallDevToolsProtocolMethodAsync("Page.createIsolatedWorld", System.Text.Json.JsonSerializer.Serialize(new { frameId, worldName = "zing-reader" })).AsTask()));
		var contextId = world.RootElement.GetProperty("executionContextId").GetInt32();
		var expression = $"{script}\n;__zingReader({System.Text.Json.JsonSerializer.Serialize(css)})";
		using var evaluated = System.Text.Json.JsonDocument.Parse(await WhileOpenAsync(core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", System.Text.Json.JsonSerializer.Serialize(new { expression, contextId, returnByValue = true })).AsTask()));
		if (evaluated.RootElement.TryGetProperty("exceptionDetails", out var failure)) throw new ReaderScriptException(failure.GetRawText());
		using var outcome = System.Text.Json.JsonDocument.Parse(evaluated.RootElement.GetProperty("result").GetProperty("value").GetString()!);
		if (!outcome.RootElement.GetProperty("ok").GetBoolean()) return outcome.RootElement.GetProperty("reason").GetString();
		InReader = true;
		return null;
	}

	/// <summary>WebView2 exposes no history list; DevTools' Page.getNavigationHistory does.</summary>
	public async Task<PageHistory> HistoryAsync()
	{
		if (!IsOpen || _core is null) return new PageHistory([], 0);
		string json;
		try
		{
			json = await WhileOpenAsync(_core.CallDevToolsProtocolMethodAsync("Page.getNavigationHistory", "{}").AsTask());
		}
		catch (ViewGoneException)
		{
			// The tab closed while the engine answered: no history, as for a page that never started.
			return new PageHistory([], 0);
		}
		using var history = System.Text.Json.JsonDocument.Parse(json);
		var root = history.RootElement;
		var entries = root.GetProperty("entries").EnumerateArray()
			.Select(e => new HistoryEntry(e.GetProperty("id").GetInt32(), e.GetProperty("url").GetString()!, e.GetProperty("title").GetString() ?? ""))
			.ToList();
		return new PageHistory(entries, root.GetProperty("currentIndex").GetInt32());
	}

	public void GoToHistoryEntry(int entryId) =>
		Run(core => Background(core.CallDevToolsProtocolMethodAsync("Page.navigateToHistoryEntry", System.Text.Json.JsonSerializer.Serialize(new { entryId })).AsTask(), "history"));

	public bool IsAudible => _core?.IsDocumentPlayingAudio ?? false;

	public async Task<System.Text.Json.JsonElement> EvaluateAsync(string script)
	{
		if (!IsOpen || _core is null) throw new InvalidOperationException("The page is not running");
		// A tab that closes meanwhile has no result (ViewGoneException).
		var json = await WhileOpenAsync(_core.ExecuteScriptAsync(script).AsTask());
		return System.Text.Json.JsonDocument.Parse(json).RootElement;
	}

	public void RunWhenLoaded(string script) => Run(core =>
	{
		void Loaded(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs e)
		{
			core.NavigationCompleted -= Loaded;
			if (e.IsSuccess) Background(core.ExecuteScriptAsync(script).AsTask(), "script");
		}
		core.NavigationCompleted += Loaded;
	});

	public void StopFind() => Run(core =>
	{
		_findTerm = null;
		core.Find.Stop();
	});

	/// <summary>The start page draws both palettes and picks one by prefers-color-scheme, which follows Zing's theme setting.</summary>
	public void FollowTheme() => Run(core => core.Profile.PreferredColorScheme = _theme() switch
	{
		"light" => CoreWebView2PreferredColorScheme.Light,
		"dark" => CoreWebView2PreferredColorScheme.Dark,
		_ => CoreWebView2PreferredColorScheme.Auto,
	});

	public void Close()
	{
		if (!IsOpen) return;
		IsOpen = false;
		_features.Left(this);
		_queued.Clear();
		_view?.Close();
	}

	private void Run(Action<CoreWebView2> action)
	{
		if (!IsOpen) return;
		if (_core is null) _queued.Add(action);
		else action(_core);
	}

	private async Task StartAsync(CoreWebView2Environment environment)
	{
		var options = environment.CreateCoreWebView2ControllerOptions();
		// Keeps nothing by default: an in-memory profile, as Electron's non-persistent partition was.
		options.IsInPrivateModeEnabled = !_persistent;
		options.ProfileName = _persistent ? "zing" : "zing-private";
		// The window's scope tracks the view from the start, so a window that closes meanwhile closes it.
		_view = await HostedView.CreateAsync(_window, _scope, environment, options, "page", overlay: false);
		if (!IsOpen)
		{
			_view.Close();
			return;
		}
		_view.Visible = _visible;
		_view.Place(_bounds);
		_core = _view.Core;
		_core.Profile.PreferredTrackingPreventionLevel = CoreWebView2TrackingPreventionLevel.Strict;
		_core.Profile.DefaultDownloadFolderPath = _features.DownloadsDirectory;
		_core.AddWebResourceRequestedFilter(StartPage.Scheme + "://*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
		_core.WebResourceRequested += OnWebResourceRequested;
		FollowTheme();
		Wire(_core);
		// A click into a split pane makes its tab the active one. The engine also moves the keyboard on its own (a page
		// shown again, the window coming back to the front); those must not change the active tab, so only a focus that
		// comes with a mouse button down counts. Panes are separate windows, so the keyboard alone never moves between them.
		_view.Controller.GotFocus += (_, _) =>
		{
			if (_view.HasKeyboard && Native.MouseButtonDown()) _events.Focused();
		};

		foreach (var action in _queued) action(_core);
		_queued.Clear();
	}

	private void Wire(CoreWebView2 core)
	{
		core.NavigationStarting += (_, e) =>
		{
			// Links a page offers are cleaned at navigation time, server redirects included.
			switch (_events.Navigating(core.Source, e.Uri))
			{
				case NavigationDecision.Cancelled:
					e.Cancel = true;
					return;
				case NavigationDecision.Redirect redirect:
					e.Cancel = true;
					core.Navigate(redirect.Url);
					return;
			}
			_navigation = e.NavigationId;
			// NavigationStarting is only raised for new documents; in-page navigations keep their grants.
			_features.Left(this);
			InReader = false;
			IsLoading = true;
			_events.StateChanged();
		};
		core.NavigationCompleted += (_, e) =>
		{
			IsLoading = false;
			if (!e.IsSuccess && e.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled) ReportFailure(e.WebErrorStatus, e.NavigationId);
			_events.StateChanged();
		};
		core.SourceChanged += (_, _) => _events.Navigated(core.Source);
		// The override dies with each document, so every new one gets it again.
		core.DOMContentLoaded += (_, _) =>
		{
			if (_textSize != Zing.Core.TextSize.Default) Background(core.ExecuteScriptAsync(Zing.Core.TextSize.Script(_textSize)).AsTask(), "text size");
		};
		core.DocumentTitleChanged += (_, _) => _events.TitleChanged(core.DocumentTitle);
		core.FaviconChanged += (_, _) => Background(IconChangedAsync(core), "favicon");
		core.HistoryChanged += (_, _) => _events.StateChanged();
		core.WindowCloseRequested += (_, _) => _events.Closed();
		core.NewWindowRequested += OnNewWindowRequested;
		core.ProcessFailed += OnProcessFailed;
		core.DownloadStarting += (_, e) =>
		{
			_downloads++;
			_features.DownloadStarting(this, e);
		};
		core.PermissionRequested += (_, e) => _features.PermissionRequested(this, e);
		core.ContainsFullScreenElementChanged += (_, _) => _features.FullScreenChanged(this);
		// ActiveMatchIndex already counts from 1, as the find bar does; it is -1 with no match.
		core.Find.MatchCountChanged += (_, _) => _events.FindResult(core.Find.MatchCount, Math.Max(0, core.Find.ActiveMatchIndex));
		core.Find.ActiveMatchIndexChanged += (_, _) => _events.FindResult(core.Find.MatchCount, Math.Max(0, core.Find.ActiveMatchIndex));
	}

	private async Task IconChangedAsync(CoreWebView2 core)
	{
		if (string.IsNullOrEmpty(core.FaviconUri))
		{
			_events.IconChanged(null);
			return;
		}
		var icon = await IconDataUrlAsync(core);
		// The tab may have closed while the icon was read.
		if (IsOpen) _events.IconChanged(icon);
	}

	/// <summary>
	/// Page work nobody awaits (TestHooks.Background). A call cut off because this page closed
	/// meanwhile is an expected outcome, like a view that went with its window; anything else is recorded.
	/// </summary>
	private void Background(Task task, string what) => TestHooks.Background(WhileOpen(task), what);

	private async Task WhileOpen(Task task)
	{
		try
		{
			await task;
		}
		catch (Exception) when (!IsOpen)
		{
		}
	}

	/// <summary>
	/// Page work a caller awaits. If the page closed meanwhile its outcome is moot (often an engine error
	/// from the closed view), so it ends in ViewGoneException, which TestHooks.Background expects.
	/// </summary>
	private async Task<T> WhileOpenAsync<T>(Task<T> work)
	{
		T result;
		try
		{
			result = await work;
		}
		catch (Exception) when (!IsOpen)
		{
			throw new ViewGoneException("page");
		}
		if (!IsOpen) throw new ViewGoneException("page");
		return result;
	}

	private async Task WhileOpenAsync(Task work)
	{
		try
		{
			await work;
		}
		catch (Exception) when (!IsOpen)
		{
			throw new ViewGoneException("page");
		}
		if (!IsOpen) throw new ViewGoneException("page");
	}

	/// <summary>The engine has already chosen and fetched the icon; Zing keeps it as a PNG data URL in memory.</summary>
	private static async Task<string?> IconDataUrlAsync(CoreWebView2 core)
	{
		using var stream = await core.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png);
		if (stream is null || stream.Size == 0) return null;
		using var memory = new MemoryStream();
		await stream.AsStreamForRead().CopyToAsync(memory);
		return "data:image/png;base64," + Convert.ToBase64String(memory.ToArray());
	}

	/// <summary>
	/// A navigation that turns into a download completes as ConnectionAborted (with no HTTP status) just
	/// before DownloadStarting is raised. So an abort is reported only if no download starts soon after.
	/// </summary>
	private void ReportFailure(CoreWebView2WebErrorStatus status, ulong navigation)
	{
		if (status != CoreWebView2WebErrorStatus.ConnectionAborted) _events.LoadFailed(status.ToString());
		else Background(ReportAbortAsync(status, navigation), "load failure");
	}

	/// <summary>
	/// An aborted load is a failure only if nothing explains it within the grace: a download it turned into, or a
	/// newer navigation that replaced it (a new tab's blank page sent straight to an address). The engine ends the
	/// replaced load before it raises the new one's NavigationStarting, so that can only be told afterwards.
	/// </summary>
	private async Task ReportAbortAsync(CoreWebView2WebErrorStatus status, ulong navigation)
	{
		var downloadsBefore = _downloads;
		await Task.Delay(DownloadGrace);
		if (IsOpen && _downloads == downloadsBefore && _navigation == navigation) _events.LoadFailed(status.ToString());
	}

	/// <summary>zing://home is generated by Zing; any other zing: page does not exist.</summary>
	private void OnWebResourceRequested(CoreWebView2 sender, CoreWebView2WebResourceRequestedEventArgs e)
	{
		CoreWebView2WebResourceResponse response;
		try
		{
			var page = _serveStartPage(e.Request.Uri);
			var body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(page.Body)).AsRandomAccessStream();
			response = sender.Environment.CreateWebResourceResponse(body, page.Status, "OK", $"Content-Type: {page.ContentType}\r\nCache-Control: {page.CacheControl}");
		}
		catch (StartPageSchemeException)
		{
			response = sender.Environment.CreateWebResourceResponse(null, 404, "Not Found", "");
		}
		e.Response = response;
	}

	/// <summary>
	/// A process of this page failed (EngineFailures). The page's own renderer, gone or hung, marks
	/// the tab, which keeps its record and offers Reload; the engine shows its error page meanwhile.
	/// A subframe's renderer, the GPU and helper processes come back by themselves, so they are only
	/// logged. The browser process takes every view of every window with it: the app rebuilds.
	/// </summary>
	private void OnProcessFailed(CoreWebView2 sender, CoreWebView2ProcessFailedEventArgs e)
	{
		// A page Zing closed may still hear its renderer go.
		if (!IsOpen) return;
		EngineFailures.Warn("page", e);
		switch (e.ProcessFailedKind)
		{
			case CoreWebView2ProcessFailedKind.RenderProcessExited:
				// A load the renderer took with it never completes; the tab mustn't spin over the error page.
				IsLoading = false;
				_events.Crashed(PageFault.Crashed);
				break;
			case CoreWebView2ProcessFailedKind.RenderProcessUnresponsive:
				_events.Crashed(PageFault.Unresponsive);
				break;
			case CoreWebView2ProcessFailedKind.BrowserProcessExited:
				EngineFailures.ReportBrowserLost();
				break;
		}
	}

	/// <summary>
	/// A popup must be the CoreWebView2 the engine asked for, not a fresh tab
	/// loading the same URL, or window.opener is null and OAuth popups (Google,
	/// Microsoft, payment pages) have nowhere to send their result.
	/// </summary>
	private void OnNewWindowRequested(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs e) =>
		Background(OpenPopupAsync(e), "popup");

	private async Task OpenPopupAsync(CoreWebView2NewWindowRequestedEventArgs e)
	{
		var deferral = e.GetDeferral();
		try
		{
			var popup = (WebView2Page)_events.Popup(e.Uri, background: false);
			await popup.Ready;
			// The opener or the popup may have closed while the engine started it: then there is no window to hand over.
			if (IsOpen && popup.IsOpen && popup.IsStarted) e.NewWindow = popup.Core;
		}
		finally
		{
			// Handled even without a NewWindow (window.open gets null), so the engine never opens an Edge window of its own.
			if (IsOpen) e.Handled = true;
			deferral.Complete();
		}
	}
}

public sealed class ReaderScriptException(string details) : Exception($"The reader script failed in the page: {details}");
