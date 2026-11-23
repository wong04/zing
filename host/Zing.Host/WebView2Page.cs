using Microsoft.Web.WebView2.Core;
using Zing.Core;
using Zing.Core.LinkPreview;
using Zing.Core.Net;

namespace Zing.Host;

/// <summary>
/// A tab's page over a WebView2. The control starts asynchronously, so calls
/// made before it is ready are queued and run in order once it is.
/// </summary>
public sealed class WebView2Page : IPage
{

	private IPageEvents _events;
	private readonly Func<string, StartPageResponse> _serveStartPage;
	private readonly IPageFeatureHost _features;
	private readonly bool _persistent;
	private readonly RelayedSite _relay;
	private readonly DocumentRuns _relayRuns = new();
	/// <summary>The address of the last navigation Zing itself started; the relay scheme loads in a tab only for such a navigation.</summary>
	private string? _zingNavigation;
	/// <summary>The relay address the main-frame load under way was allowed for, so only that document is served.</summary>
	private string? _approvedDocument;
	/// <summary>The address of a navigation that was turned into a warning: the engine still sends its request, which is refused.</summary>
	private string? _warnedDocument;
	private bool _chainIsRelay;
	/// <summary>A popup of a relayed page: relayed from its birth, while it is still on its blank first document.</summary>
	private bool _bornRelayed;
	private static readonly TimeSpan AppLikeWait = TimeSpan.FromMilliseconds(1500);
	private static readonly TimeSpan HangCheckEvery = TimeSpan.FromSeconds(3);
	private readonly List<Action<CoreWebView2>> _queued = [];
	/// <summary>The queue has run: calls go straight to the engine. Until then they queue, even once the core exists, so none overtakes an earlier one while the page's scripts are added.</summary>
	private bool _drained;
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
	/// <summary>Downloads this page started that are still coming in; its engine view outlives the page until they end.</summary>
	private int _downloading;
	private ulong _navigation;
	private static readonly TimeSpan DownloadGrace = TimeSpan.FromSeconds(1);
	/// <summary>A real document has started to show (ContentLoading for anything but the blank first one).</summary>
	private bool _showedDocument;
	/// <summary>The engine opened this page for another one's window request (a link to a new tab, window.open).</summary>
	private bool _openedForPage;
	/// <summary>The main frame's DevTools id, once Page.getFrameTree has answered; frames' navigation reasons are not the page's.</summary>
	private string? _mainFrameId;
	/// <summary>The latest main-frame Page.frameRequestedNavigation: it arrives before NavigationStarting for the same address (spikes/link-preview, check C).</summary>
	private (string Url, string Reason)? _lastReason;
	private bool _dialogOpen;
	/// <summary>The renderer was reported unresponsive and hasn't answered since.</summary>
	private bool _hung;
	/// <summary>Large request bodies still going out, from DevTools' Network events: nothing runs in the page for it.</summary>
	private readonly UploadTracker _uploads = new();
	private ViewerGuests? _viewers;
	private readonly FrameNavigations _frames = new(() => DateTime.UtcNow);

	private static readonly Lazy<(string Script, string Css)> Reader = new(() =>
	{
		var dir = Path.Combine(AppContext.BaseDirectory, "Assets");
		return (File.ReadAllText(Path.Combine(dir, "reader.js")), File.ReadAllText(Path.Combine(dir, "reader.css")));
	});

	public WebView2Page(HostWindow window, WindowScope scope, IPageEvents events, CoreWebView2Environment environment, Func<string, StartPageResponse> serveStartPage, IPageFeatureHost features, bool persistent, RelayedSite relay)
	{
		_relay = relay;
		_window = window;
		_scope = scope;
		_events = events;
		_serveStartPage = serveStartPage;
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
			if (_view is null) return;
			// Showing the view resumes a suspended page (TrySuspendAsync).
			_view.Visible = value;
			if (_core is { } core) FollowMemoryTarget(core);
		}
	}

	/// <summary>
	/// A page off screen asks the engine to trim its memory, and gets it back when shown: the engine never raises the level by
	/// itself. A page whose video is in picture-in-picture, or that plays sound, is still in use, so it keeps its memory.
	/// </summary>
	private void FollowMemoryTarget(CoreWebView2 core)
	{
		if (_visible)
		{
			core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Normal;
			return;
		}
		Background(LowerMemoryTargetAsync(core), "memory target");
	}

	private async Task LowerMemoryTargetAsync(CoreWebView2 core)
	{
		// A page that has shown no document yet (a tab opened in the background) has nothing in picture-in-picture.
		if (core.IsDocumentPlayingAudio || _showedDocument && await InPictureInPictureAsync(core)) return;
		// Shown again, or closed, while the page answered.
		if (!IsOpen || _visible) return;
		core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;
	}

	/// <summary>Whether the page's video plays in a picture-in-picture window, which stays on screen while the tab is hidden.</summary>
	private async Task<bool> InPictureInPictureAsync(CoreWebView2 core) =>
		await WhileOpenAsync(core.ExecuteScriptAsync("document.pictureInPictureElement !== null").AsTask()) == "true";

	public bool IsSuspended => IsOpen && _core?.IsSuspended == true;

	/// <summary>
	/// Freezes the hidden page's renderer, which keeps the page but frees its CPU and lets Windows reuse its memory; showing
	/// the view resumes it. Refused for a page on screen, in picture-in-picture, playing sound, or still being pictured.
	/// </summary>
	public async Task<bool> TrySuspendAsync()
	{
		if (!IsOpen || _core is not { } core || _visible || core.IsSuspended) return false;
		// A hidden page never finishes a capture, and its renderer must not go while one is pending (CapturesInFlight).
		if (PendingCapture is { IsCompleted: false } || core.IsDocumentPlayingAudio) return false;
		try
		{
			if (await InPictureInPictureAsync(core)) return false;
			// The engine throws for a visible view: shown again while the page answered.
			if (!IsOpen || _visible) return false;
			return await WhileOpenAsync(core.TrySuspendAsync().AsTask());
		}
		catch (ViewGoneException)
		{
			// The tab slept or closed while the engine answered: no page left to suspend.
			return false;
		}
	}

	/// <summary>Completes once the engine has a CoreWebView2 for this page; a popup hands it to the opener's request.</summary>
	public Task Ready { get; }

	public CoreWebView2 Core => _core ?? throw new InvalidOperationException("The page has not started");

	public bool IsOpen { get; private set; } = true;

	public void Rebind(IPageEvents events) => _events = events;

	public bool IsStarted => _core is not null;

	public string Url => _core?.Source ?? "";
	public string Title => _core?.DocumentTitle ?? "";
	public bool IsLoading { get; private set; }
	public bool CanGoBack => _core?.CanGoBack ?? false;
	public bool CanGoForward => _core?.CanGoForward ?? false;

	public void Navigate(string url)
	{
		_zingNavigation = url;
		Run(core => core.Navigate(url));
	}

	/// <summary>A popup a relayed page opened: the engine starts its first navigation, which Zing has already judged.</summary>
	public void AllowFirstNavigation(string url)
	{
		_zingNavigation = url;
		_bornRelayed = true;
	}

	/// <summary>
	/// The address that decides what a page may do: the page's own, except that a relayed page's popup counts as relayed before its first
	/// load commits. Its opener can already navigate it then, and it would otherwise look like any blank page.
	/// </summary>
	private string TopOf(CoreWebView2 core) =>
		_bornRelayed && core.Source is "" or "about:blank" ? RelayedContent.Scheme + "://popup.invalid/" : core.Source;
	/// <summary>Like the warning page's Replace decision: the engine has no navigation that replaces, but the document's location does.</summary>
	public void Replace(string url)
	{
		_zingNavigation = url;
		Run(core => Background(core.ExecuteScriptAsync($"location.replace({System.Text.Json.JsonSerializer.Serialize(url)})").AsTask(), "history step"));
	}

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

	/// <summary>
	/// A picture's widest, about twice an overview card. The engine draws the whole view, and every overview push while it
	/// is open carries every picture: at full size five tabs made 4.7 MB per push and some 400 ms on the UI thread each.
	/// </summary>
	private const uint PictureWidth = 640;

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
		CapturesTaken++;
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
		using var picture = await ShrinkAsync(stream);
		using var memory = new MemoryStream();
		picture.Seek(0);
		await picture.AsStreamForRead().CopyToAsync(memory);
		return "data:image/jpeg;base64," + Convert.ToBase64String(memory.ToArray());
	}

	/// <summary>The engine's JPEG at most <see cref="PictureWidth"/> wide, re-encoded off the UI thread.</summary>
	private static async Task<Windows.Storage.Streams.InMemoryRandomAccessStream> ShrinkAsync(Windows.Storage.Streams.IRandomAccessStream full)
	{
		full.Seek(0);
		var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(full);
		var scale = Math.Min(1.0, (double)PictureWidth / decoder.PixelWidth);
		var shrunk = new Windows.Storage.Streams.InMemoryRandomAccessStream();
		var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateForTranscodingAsync(shrunk, decoder);
		encoder.BitmapTransform.ScaledWidth = (uint)Math.Max(1, Math.Round(decoder.PixelWidth * scale));
		encoder.BitmapTransform.ScaledHeight = (uint)Math.Max(1, Math.Round(decoder.PixelHeight * scale));
		encoder.BitmapTransform.InterpolationMode = Windows.Graphics.Imaging.BitmapInterpolationMode.Fant;
		await encoder.FlushAsync();
		return shrunk;
	}

	/// <summary>Edge's own print preview, inside the page; an e2e run records it instead.</summary>
	public void Print() => Run(core =>
	{
		if (TestHooks.IsEndToEndRun) TestHooks.RecordDialog("print", core.Source);
		else core.ShowPrintUI(CoreWebView2PrintDialogKind.Browser);
	});

	public bool InReader { get; private set; }

	/// <summary>Whether an element of the page, or its PDF viewer (ViewerGuests), is fullscreen; the window then gives the page all of itself.</summary>
	// A closed page has none, and its engine object would throw: a window closing with a preview open still asks its layers.
	public bool IsFullScreen => IsOpen && (_core?.ContainsFullScreenElement == true || _viewers?.IsFullScreen == true);

	/// <summary>
	/// From the DevTools Page domain (Page.enable is on for the navigation reasons): javascriptDialogOpening and javascriptDialogClosed,
	/// for any frame of the page. A dialog can't outlive its document, so a new one clears it too, and a missed Closed can't hold it.
	/// </summary>
	public bool HasOpenDialog => IsOpen && _dialogOpen;

	/// <summary>Whether the page's PDF viewer has made itself fullscreen with its own button (ViewerGuests).</summary>
	public bool InViewerFullScreen => IsOpen && _viewers?.IsFullScreen == true;

	/// <summary>Takes the page's element out of fullscreen, as the page's own exit would.</summary>
	public void ExitFullScreen()
	{
		if (!IsOpen) return;
		if (_core?.ContainsFullScreenElement == true) Run(core => Background(core.ExecuteScriptAsync("document.exitFullscreen()").AsTask(), "exit fullscreen"));
		if (_viewers?.IsFullScreen == true) _viewers.Exit();
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

	public bool IsUploading => _uploads.IsUploading;

	public PageHistory History { get; private set; } = new([], 0);

	/// <summary>
	/// Keeps <see cref="History"/> current for the session and a tab that sleeps, then says so, so the session is saved with it.
	/// DevTools can refuse while the main frame changes hosts (an error result, which WebView2 raises as ArgumentException): the
	/// last answer stands until the next change, and the tab's own address and title stand for the entry it is on.
	/// </summary>
	private async Task FollowHistoryAsync()
	{
		try
		{
			History = await HistoryAsync();
		}
		catch (ArgumentException error)
		{
			Faults.Warning("history snapshot", error);
			return;
		}
		if (IsOpen) _events.StateChanged();
	}

	/// <summary>
	/// Network.enable with no bodies kept: only the events are wanted (<see cref="UploadTracker"/>). They come from the engine, so
	/// the page sees nothing, unlike wrapping XMLHttpRequest or fetch.
	/// </summary>
	private async Task FollowUploadsAsync(CoreWebView2 core)
	{
		core.GetDevToolsProtocolEventReceiver("Network.requestWillBeSentExtraInfo").DevToolsProtocolEventReceived += (_, e) => TestHooks.Guard(() => _uploads.HeadersSent(e.ParameterObjectAsJson), "upload start");
		core.GetDevToolsProtocolEventReceiver("Network.loadingFinished").DevToolsProtocolEventReceived += (_, e) => TestHooks.Guard(() => _uploads.Ended(e.ParameterObjectAsJson), "upload end");
		core.GetDevToolsProtocolEventReceiver("Network.loadingFailed").DevToolsProtocolEventReceived += (_, e) => TestHooks.Guard(() => _uploads.Ended(e.ParameterObjectAsJson), "upload end");
		await WhileOpenAsync(core.CallDevToolsProtocolMethodAsync("Network.enable", System.Text.Json.JsonSerializer.Serialize(new { maxTotalBufferSize = 0, maxResourceBufferSize = 0, maxPostDataSize = 0 })).AsTask());
	}

	public async Task<System.Text.Json.JsonElement> EvaluateAsync(string script)
	{
		if (!IsOpen || _core is null) throw new InvalidOperationException("The page is not running");
		// A suspended renderer would answer only once shown; the caller (the sleep probe) needs the answer now.
		if (_core.IsSuspended) _core.Resume();
		// A tab that closes meanwhile has no result (ViewGoneException).
		var json = await WhileOpenAsync(_core.ExecuteScriptAsync(script).AsTask());
		return System.Text.Json.JsonDocument.Parse(json).RootElement;
	}

	/// <summary>
	/// DevTools' isolated worlds: the page's scripts can't see what runs there. Asking for <see cref="PageProbe.World"/> in
	/// a frame gives back the world its document-start script ran in. Frames of other origins are left out.
	/// </summary>
	public async Task<IReadOnlyList<System.Text.Json.JsonElement>> EvaluateInFramesAsync(string script)
	{
		if (!IsOpen || _core is not { } core) throw new InvalidOperationException("The page is not running");
		if (core.IsSuspended) core.Resume();
		using var tree = System.Text.Json.JsonDocument.Parse(await WhileOpenAsync(core.CallDevToolsProtocolMethodAsync("Page.getFrameTree", "{}").AsTask()));
		var root = tree.RootElement.GetProperty("frameTree");
		var origin = root.GetProperty("frame").GetProperty("securityOrigin").GetString();
		var results = new List<System.Text.Json.JsonElement>();
		foreach (var frame in FramesOf(root).Where(f => f.GetProperty("securityOrigin").GetString() == origin))
		{
			var world = System.Text.Json.JsonSerializer.Serialize(new { frameId = frame.GetProperty("id").GetString(), worldName = PageProbe.World });
			using var created = System.Text.Json.JsonDocument.Parse(await WhileOpenAsync(core.CallDevToolsProtocolMethodAsync("Page.createIsolatedWorld", world).AsTask()));
			var evaluate = System.Text.Json.JsonSerializer.Serialize(new { expression = script, contextId = created.RootElement.GetProperty("executionContextId").GetInt32(), returnByValue = true });
			using var answer = System.Text.Json.JsonDocument.Parse(await WhileOpenAsync(core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", evaluate).AsTask()));
			if (answer.RootElement.TryGetProperty("exceptionDetails", out var error)) throw new InvalidOperationException($"The script failed in a frame: {error.GetRawText()}");
			results.Add(answer.RootElement.GetProperty("result").GetProperty("value").Clone());
		}
		return results;
	}

	/// <summary>A DevTools call on the page's own session (CdpPageDriver); throws <see cref="ViewGoneException"/> if the page closes meanwhile.</summary>
	public async Task<string> DevToolsAsync(string method, string json)
	{
		if (!IsOpen || _core is not { } core) throw new ViewGoneException("page");
		// A suspended renderer would answer only once shown.
		if (core.IsSuspended) core.Resume();
		return await WhileOpenAsync(core.CallDevToolsProtocolMethodAsync(method, json).AsTask());
	}

	/// <summary>Calls <paramref name="received"/> with each DevTools <paramref name="eventName"/> on the page's own session, for the page's life (CdpPageDriver).</summary>
	public void OnDevToolsEvent(string eventName, Action<string> received)
	{
		if (!IsOpen || _core is not { } core) throw new ViewGoneException("page");
		core.GetDevToolsProtocolEventReceiver(eventName).DevToolsProtocolEventReceived += (_, e) => TestHooks.Guard(() => received(e.ParameterObjectAsJson), eventName);
	}

	private static IEnumerable<System.Text.Json.JsonElement> FramesOf(System.Text.Json.JsonElement node)
	{
		yield return node.GetProperty("frame");
		if (!node.TryGetProperty("childFrames", out var children)) yield break;
		foreach (var child in children.EnumerateArray())
			foreach (var frame in FramesOf(child)) yield return frame;
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

	public void Close()
	{
		if (!IsOpen) return;
		IsOpen = false;
		// Before its dialogs go (Left), which would answer a leave check's prompt Stay: a page that closes has left.
		EndLeave(true);
		_features.Left(this);
		_queued.Clear();
		_relayRuns.Dispose();
		// A download's operation stops working once the view that started it closes (no more events, and Cancel fails),
		// though the file still comes in. So a page closed mid-download keeps its view, hidden and silent, until they end.
		if (_downloading > 0 && _view is { } held && _core is { } core)
		{
			held.Visible = false;
			core.IsMuted = true;
			return;
		}
		_view?.Close();
	}

	/// <summary>
	/// Where a leave check navigates: Chromium runs the document's beforeunload first, raising its "Leave site?" prompt
	/// (OnScriptDialog) when the page asks and the person has used it, as Chrome requires. The navigation starting means
	/// the page may go, and it is always cancelled, so the page stays as it was and loses nothing if the close is called off.
	/// </summary>
	internal const string LeaveProbe = "about:blank?zing-leave-check";

	/// <summary>A leave check under way: whether the person is asked (a close) or not (the sleep probe), and how it ended.</summary>
	private sealed class LeaveRun
	{
		/// <summary>True when the page may go: it asked nothing, or the person chose Leave.</summary>
		public TaskCompletionSource<bool> Left { get; } = new();
		public bool AsksPerson { get; set; }
		/// <summary>The close was called off; a prompt that still comes is answered Stay without a word.</summary>
		public bool Abandoned { get; set; }
	}

	private LeaveRun? _leave;

	public Task<bool> RunBeforeUnloadAsync(CancellationToken abandon)
	{
		if (BeginLeave(abandon) is not { } run) return Task.FromResult(true);
		run.AsksPerson = true;
		return run.Left.Task;
	}

	public async Task<bool> AsksBeforeLeavingAsync(CancellationToken abandon) => BeginLeave(abandon) is { } run && !await run.Left.Task;

	/// <summary>
	/// The leave check under way (a second close waits on its prompt), or a new one; null when the page has nothing
	/// beforeunload could keep: no web document, a renderer that stopped answering, or a dialog of its own already up (its
	/// script is blocked, so it can't ask).
	/// </summary>
	private LeaveRun? BeginLeave(CancellationToken abandon)
	{
		if (!IsOpen || _core is not { } core || !_drained) return null;
		if (_leave is not { Abandoned: false } run)
		{
			if (_hung || _dialogOpen) return null;
			if (!core.Source.StartsWith("http:", StringComparison.OrdinalIgnoreCase) && !core.Source.StartsWith("https:", StringComparison.OrdinalIgnoreCase)) return null;
			run = new LeaveRun();
			_leave = run;
			core.Navigate(LeaveProbe);
		}
		// Back on the UI thread, where the page's state lives, whichever thread cancels.
		abandon.Register(() =>
		{
			run.Abandoned = true;
			run.Left.TrySetResult(false);
		}, useSynchronizationContext: true);
		return run;
	}

	/// <summary>The leave check under way is over: the page may go (<paramref name="left"/>), or stays.</summary>
	private void EndLeave(bool left)
	{
		if (_leave is not { } run) return;
		_leave = null;
		run.Left.TrySetResult(left);
	}

	/// <summary>
	/// A dialog of any frame of the page. A "Leave site?" the sleep probe raised, or one for a close that was called off, is
	/// answered Stay at once; everything else goes to the window's dialogs, and the page waits on the deferral.
	/// </summary>
	private void OnScriptDialog(CoreWebView2ScriptDialogOpeningEventArgs e)
	{
		var leaving = e.Kind == CoreWebView2ScriptDialogKind.Beforeunload ? _leave : null;
		if (leaving is { AsksPerson: false } or { Abandoned: true })
		{
			// Not accepted: the navigation is cancelled and the page stays.
			EndLeave(false);
			return;
		}
		var kind = e.Kind switch
		{
			CoreWebView2ScriptDialogKind.Alert => ScriptDialogKinds.Alert,
			CoreWebView2ScriptDialogKind.Confirm => ScriptDialogKinds.Confirm,
			CoreWebView2ScriptDialogKind.Prompt => ScriptDialogKinds.Prompt,
			CoreWebView2ScriptDialogKind.Beforeunload => ScriptDialogKinds.BeforeUnload,
			_ => throw new ArgumentOutOfRangeException(nameof(e), e.Kind, "Not a script dialog kind"),
		};
		_features.ScriptDialog(this, kind, e.Uri, e.Message, e.DefaultText, new DialogAnswer(this, e, e.GetDeferral(), leaving));
	}

	/// <summary>The person's answer, given to the engine's waiting dialog; a page that has closed meanwhile is past answering.</summary>
	private sealed class DialogAnswer(WebView2Page page, CoreWebView2ScriptDialogOpeningEventArgs e, Windows.Foundation.Deferral deferral, LeaveRun? leaving) : IScriptDialogAnswer
	{
		public void Accept(string? text)
		{
			if (!page.IsOpen) return;
			if (text is not null) e.ResultText = text;
			e.Accept();
			// Leave: the leave check's navigation now starts, which ends it (NavigationStarting).
			deferral.Complete();
		}

		public void Dismiss()
		{
			if (leaving is not null && page._leave == leaving) page.EndLeave(false);
			if (!page.IsOpen) return;
			deferral.Complete();
		}
	}

	/// <summary>A download this page started is under way: the engine view stays until <see cref="DownloadEnded"/>.</summary>
	public void DownloadStarted() => _downloading++;

	public void DownloadEnded()
	{
		if (_downloading == 0) throw new InvalidOperationException("A download ended that this page never started");
		_downloading--;
		if (_downloading == 0 && !IsOpen) _view?.Close();
	}

	private void Run(Action<CoreWebView2> action)
	{
		if (!IsOpen) return;
		if (_core is null || !_drained) _queued.Add(action);
		else action(_core);
	}

	/// <summary>The on-disk browsing profile's name; with persistentProfile off, Zing deletes it at launch (ZingApp.OpenEnvironmentAsync).</summary>
	public const string KeptProfileName = "zing";

	/// <summary>
	/// The person's browsing profile, which tabs and the Google sign-in window (SignInPopup) use. Keeps nothing by default: an
	/// in-memory profile, as Electron's non-persistent partition was; <paramref name="persistent"/> is the opt-in on-disk one.
	/// </summary>
	public static CoreWebView2ControllerOptions BrowsingProfile(CoreWebView2Environment environment, bool persistent)
	{
		var options = environment.CreateCoreWebView2ControllerOptions();
		options.IsInPrivateModeEnabled = !persistent;
		options.ProfileName = persistent ? KeptProfileName : "zing-private";
		return options;
	}

	private async Task StartAsync(CoreWebView2Environment environment)
	{
		var options = BrowsingProfile(environment, _persistent);
		// The window's scope tracks the view from the start, so a window that closes meanwhile closes it.
		_view = await HostedView.CreateAsync(_window, _scope, environment, options, "page", overlay: false);
		if (!IsOpen)
		{
			_view.Close();
			return;
		}
		_view.Visible = _visible;
		_view.Place(_bounds);
		_features.ViewStarted(this);
		_core = _view.Core;
		FollowMemoryTarget(_core);
		// A page is not Zing's UI. No host object is added, and the only page messages anything listens for are the autofill
		// script's (OnPageMessage), and only while the vault is on: with it off, web messages stay off as belt and braces
		// (FollowAutofillAsync). chrome.webview itself stays on the page (the engine injects it) and postMessage still returns
		// without error, as the relayed-page e2e observes.
		_core.Settings.AreHostObjectsAllowed = false;
		// Alert, confirm, prompt and "Leave site?" are drawn by Zing in the page UI (ScriptDialogs), never by the engine, so a
		// close can run the page's beforeunload and hear its answer (RunBeforeUnloadAsync). Read as each document loads.
		_core.Settings.AreDefaultScriptDialogsEnabled = false;
		FollowTrackerBlocking();
		_core.Profile.DefaultDownloadFolderPath = _features.DownloadsDirectory;
		_core.AddWebResourceRequestedFilter(StartPage.Scheme + "://*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
		// The file viewer's address belongs to Zing's own views; a page asking for it gets a 404, never a file (OnWebResourceRequested).
		_core.AddWebResourceRequestedFilter($"https://{Zing.Core.Viewer.ViewerFiles.Host}/*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
		_core.AddWebResourceRequestedFilter(RelayedContent.Scheme + "://*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
		// A cancelled navigation has already sent its request by the time it is cancelled, so documents are held back here, before the network.
		_core.AddWebResourceRequestedFilter("http://*", CoreWebView2WebResourceContext.Document, CoreWebView2WebResourceRequestSourceKinds.All);
		_core.AddWebResourceRequestedFilter("https://*", CoreWebView2WebResourceContext.Document, CoreWebView2WebResourceRequestSourceKinds.All);
		// Every page request runs this on the UI thread; an exception out of an engine event would end the process.
		_core.WebResourceRequested += (sender, e) => TestHooks.Guard(() => OnWebResourceRequested(sender, e), "resource request");
		Wire(_core);
		// A click into a split pane makes its tab the active one. The engine also moves the keyboard on its own (a page
		// shown again, the window coming back to the front); those must not change the active tab, so only a focus that
		// comes with a mouse button down counts. Panes are separate windows, so the keyboard alone never moves between them.
		_view.Controller.GotFocus += (_, _) =>
		{
			if (_view.HasKeyboard && Native.MouseButtonDown()) _events.Focused();
		};

		// Before any navigation, so every document notes typing from its start (PageProbe.WorldSource).
		await _core.CallDevToolsProtocolMethodAsync("Page.addScriptToEvaluateOnNewDocument", System.Text.Json.JsonSerializer.Serialize(new { source = PageProbe.WorldSource, worldName = PageProbe.World }));
		if (!IsOpen) return;
		_core.WebMessageReceived += OnPageMessage;
		// Before any queued navigation, so every document gets the script; it installs itself in the top frame only.
		await FollowAutofillAsync();
		if (!IsOpen) return;
		if (_persistent)
		{
			await _features.AttachExtensionsAsync(_core.Profile);
			if (!IsOpen) return;
		}

		// By index: an action can call back into Run (a focus that activates a tab), which queues behind it until the end.
		for (var i = 0; i < _queued.Count; i++) _queued[i](_core);
		_queued.Clear();
		_drained = true;
	}

	/// <summary>
	/// Edge's tracking prevention is Balanced while blockTrackers is on and off with it. EasyList, EasyPrivacy and the tracker
	/// list already refuse the trackers; Strict on top of them also blocks what sites need from other hosts (sign-in
	/// widgets, embeds), which Balanced leaves to sites the person uses. The level is the profile's, so it applies to every page on it.
	/// </summary>
	public void FollowTrackerBlocking()
	{
		if (!IsOpen || _core is not { } core) return;
		var blocking = _features.BlockTrackers;
		core.Profile.PreferredTrackingPreventionLevel = blocking ? CoreWebView2TrackingPreventionLevel.Balanced : CoreWebView2TrackingPreventionLevel.None;
		if (blocking == _blockableFiltered) return;
		_blockableFiltered = blocking;
		// The kinds of request ads and trackers make, which OnWebResourceRequested refuses while blockTrackers is on. The
		// lists name about 100,000 hosts, too many for a filter each, so every such request comes here, on the UI thread;
		// with blocking off nothing would be refused, so they don't come at all. Only these kinds, and only from pages: with
		// every kind from every source (CoreWebView2WebResourceContext.All), one run saw the engine's browser process fail
		// an assertion (0x80000003) as a tab with a camera request closed (guards.spec.ts).
		foreach (var context in BlockableContexts)
		{
			foreach (var pattern in (string[])["http://*", "https://*"])
			{
				if (blocking) core.AddWebResourceRequestedFilter(pattern, context, CoreWebView2WebResourceRequestSourceKinds.Document);
				else core.RemoveWebResourceRequestedFilter(pattern, context, CoreWebView2WebResourceRequestSourceKinds.Document);
			}
		}
	}

	/// <summary>Whether the blockable kinds of request are sent to OnWebResourceRequested (FollowTrackerBlocking).</summary>
	private bool _blockableFiltered;

	/// <summary>Requests of the blockable kinds that reached OnWebResourceRequested, in every window (TestHooks "work-counts").</summary>
	public static int BlockableRequests { get; private set; }

	/// <summary>Pictures asked of the engine, in every window (TestHooks "work-counts").</summary>
	public static int CapturesTaken { get; private set; }

	/// <summary>Whether the autofill channel is on for this page: web messages, the script, and OnPageMessage reading them.</summary>
	private bool _autofill;
	/// <summary>The detection script's id while it is added for new documents.</summary>
	private string? _autofillScript;
	private bool _addingAutofillScript;

	/// <summary>
	/// Follows passwordAutofill (Ruling 30): with it on, web messages are enabled and every new document gets the detection
	/// script; with it off, both go, and OnPageMessage drops whatever still arrives. A document already loaded keeps the
	/// script it started with until it reloads, but nothing it posts is read.
	/// </summary>
	public async Task FollowAutofillAsync()
	{
		if (!IsOpen || _core is not { } core) return;
		var on = _features.AutofillOn;
		_autofill = on;
		core.Settings.IsWebMessageEnabled = on;
		if (!on)
		{
			if (_autofillScript is not { } added) return;
			_autofillScript = null;
			core.RemoveScriptToExecuteOnDocumentCreated(added);
			return;
		}
		if (_autofillScript is not null || _addingAutofillScript) return;
		_addingAutofillScript = true;
		string id;
		try
		{
			id = await core.AddScriptToExecuteOnDocumentCreatedAsync(Zing.Core.Vault.AutofillScripts.Detection);
		}
		finally
		{
			_addingAutofillScript = false;
		}
		if (!IsOpen) return;
		// Turned off again while the script was being added.
		if (!_autofill)
		{
			core.RemoveScriptToExecuteOnDocumentCreated(id);
			return;
		}
		_autofillScript = id;
	}

	/// <summary>
	/// The autofill script's messages, from the top document only (frames post to their own CoreWebView2Frame, which nothing
	/// here listens to). The page can post the same thing, so the origin is the engine's <c>Source</c> for the message, never
	/// anything in it. Anything that isn't under the script's key is some other page message and is ignored; one that is too
	/// big, too deep or the wrong shape is dropped and counted, never thrown or logged per message (Ruling 29).
	/// </summary>
	private void OnPageMessage(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs e)
	{
		if (!_autofill) return;
		switch (Zing.Core.Vault.AutofillMessage.Read(e.WebMessageAsJson, out var message))
		{
			case Zing.Core.Vault.PageMessageVerdict.NotAutofill:
				return;
			case Zing.Core.Vault.PageMessageVerdict.Dropped:
				TestHooks.PageMessageDropped();
				return;
		}
		if (!Uri.TryCreate(e.Source, UriKind.Absolute, out var source))
		{
			Faults.Warning("autofill message", new UriFormatException($"The engine gave a message source that is not an address: {e.Source}"));
			return;
		}
		if (message is Zing.Core.Vault.AutofillMessage.Focused focused) message = InWindow(focused.Field);
		_events.Autofill(source, message!);
	}

	/// <summary>
	/// The field in DIPs of the window's content: the page's place plus the rect at its zoom, cut to the page's own bounds,
	/// which go with it as the picker's limits. A field wholly outside the page (a forged rect) has no picker.
	/// </summary>
	private Zing.Core.Vault.AutofillMessage InWindow(Zing.Core.Vault.CredentialField field)
	{
		var zoom = Zoom;
		var css = field.Rect;
		var left = Math.Max(_bounds.X, _bounds.X + css.X * zoom);
		var top = Math.Max(_bounds.Y, _bounds.Y + css.Y * zoom);
		var right = Math.Min(_bounds.X + _bounds.Width, _bounds.X + (css.X + css.Width) * zoom);
		var bottom = Math.Min(_bounds.Y + _bounds.Height, _bounds.Y + (css.Y + css.Height) * zoom);
		if (right <= left || bottom <= top) return new Zing.Core.Vault.AutofillMessage.Left();
		return new Zing.Core.Vault.AutofillMessage.Focused(field with { Rect = new Rect(left, top, right - left, bottom - top), Viewport = _bounds });
	}

	private void Wire(CoreWebView2 core)
	{
		core.GetDevToolsProtocolEventReceiver("Page.frameRequestedNavigation").DevToolsProtocolEventReceived += (_, e) => TestHooks.Guard(() => NavigationRequested(e.ParameterObjectAsJson), "navigation reason");
		core.GetDevToolsProtocolEventReceiver("Page.javascriptDialogOpening").DevToolsProtocolEventReceived += (_, _) => _dialogOpen = true;
		core.GetDevToolsProtocolEventReceiver("Page.javascriptDialogClosed").DevToolsProtocolEventReceived += (_, _) => _dialogOpen = false;
		Background(ReadNavigationReasonsAsync(core), "navigation reasons");
		Background(FollowUploadsAsync(core), "upload watch");
		core.NavigationStarting += (_, e) =>
		{
			// The leave check's own navigation: beforeunload let the page go. It never happens; the page stays as it was.
			if (e.Uri == LeaveProbe)
			{
				e.Cancel = true;
				EndLeave(true);
				return;
			}
			// Decided here and now: NavigationStarting has no deferral, and the reason has already arrived.
			var cause = _lastReason is { Url: var url, Reason: var reason } && url == e.Uri && !e.IsRedirected
				? reason switch
				{
					"anchorClick" => NavigationCause.AnchorClick,
					"formSubmissionPost" => NavigationCause.FormPost,
					_ => NavigationCause.Other,
				}
				: NavigationCause.Other;
			_lastReason = null;
			var origin = e.NavigationKind switch
			{
				CoreWebView2NavigationKind.Reload => NavigationOrigin.Reload,
				CoreWebView2NavigationKind.BackOrForward => NavigationOrigin.History,
				_ => _zingNavigation is { } started && RelayedNavigation.Same(started, e.Uri) ? NavigationOrigin.Zing : NavigationOrigin.Page,
			};
			if (!e.IsRedirected) _zingNavigation = null;
			var relayDecision = RelayedNavigation.MainFrame(new MainFrameNavigation(TopOf(core), e.Uri, origin, e.IsRedirected, _chainIsRelay));
			if (relayDecision is NavigationDecision.Redirect inRelay)
			{
				e.Cancel = true;
				_zingNavigation = inRelay.Url;
				core.Navigate(inRelay.Url);
				return;
			}
			if (relayDecision is NavigationDecision.Cancelled)
			{
				e.Cancel = true;
				if (origin == NavigationOrigin.Page && RelayedNavigation.PlainHttpNotice(TopOf(core), e.Uri) is { } refused) _events.Notice(refused);
				return;
			}
			// Links a page offers are cleaned at navigation time, server redirects included.
			switch (_events.Navigating(core.Source, e.Uri, cause))
			{
				case NavigationDecision.Cancelled:
					e.Cancel = true;
					return;
				case NavigationDecision.Redirect redirect:
					e.Cancel = true;
					// The host's own decision (a cleaned address, the warning, Visit anyway), so a relayed top must not take it for the page's: the relay rule above already refused what the page started.
					_zingNavigation = redirect.Url;
					_warnedDocument = redirect.Url.StartsWith(StartPage.Scheme + "://", StringComparison.OrdinalIgnoreCase) ? e.Uri : null;
					core.Navigate(redirect.Url);
					return;
				case NavigationDecision.Replace replace:
					e.Cancel = true;
					_zingNavigation = replace.Url;
					_warnedDocument = null;
					Background(core.ExecuteScriptAsync($"location.replace({System.Text.Json.JsonSerializer.Serialize(replace.Url)})").AsTask(), "replacing the document");
					return;
			}
			// The warning page itself starts after the cancelled navigation and must not lift the refusal.
			if (!e.Uri.StartsWith(StartPage.Scheme + "://", StringComparison.OrdinalIgnoreCase)) _warnedDocument = null;
			_navigation = e.NavigationId;
			// A new document: its sign-in fields are not the last one's, so a picker for them goes.
			if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var leaving)) _events.Autofill(leaving, new Zing.Core.Vault.AutofillMessage.Left());
			// A new document: what the last one was still asking for is no longer wanted.
			_relayRuns.LeaveDocument();
			var toRelay = RelayedContent.IsRelayUrl(e.Uri);
			if (!e.IsRedirected) _chainIsRelay = toRelay;
			_approvedDocument = e.Uri;
			// The document a leave check was asking about is going anyway; before Left, which would answer its prompt Stay.
			EndLeave(true);
			// NavigationStarting is only raised for new documents; in-page navigations keep their grants and dialogs.
			_features.Left(this);
			// The new document clears the tab's mark (TabService), and a renderer that starts one is answering.
			_hung = false;
			InReader = false;
			IsLoading = true;
			_events.StateChanged();
		};
		core.FrameNavigationStarting += (_, e) =>
		{
			// NavigationStarting doesn't see an iframe: one onto the relay from a web page would be served as a fetch on its say.
			if (!RelayedNavigation.FrameAllowed(TopOf(core), e.Uri)) e.Cancel = true;
		};
		// Every embedded frame, however deep: a download from one is judged as a frame's (FrameDownload).
		core.FrameCreated += (_, e) => FollowFrame(e.Frame);
		core.NavigationCompleted += (_, e) =>
		{
			IsLoading = false;
			if (e.IsSuccess) _viewers?.Loaded();
			if (e.IsSuccess && RelayedContent.IsRelayUrl(core.Source)) Background(CheckForAppAsync(core, e.NavigationId), "relayed app check");
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
		core.HistoryChanged += (_, _) =>
		{
			_events.StateChanged();
			Background(FollowHistoryAsync(), "history");
		};
		core.WindowCloseRequested += (_, _) => _events.Closed();
		core.NewWindowRequested += OnNewWindowRequested;
		core.ProcessFailed += OnProcessFailed;
		core.ContentLoading += (_, _) =>
		{
			if (core.Source is not ("" or "about:blank")) _showedDocument = true;
			_dialogOpen = false;
			// Raised for new documents only, however they started (a link, Back, a reload, the page's own script).
			_events.NewDocument();
			_viewers?.Left();
		};
		core.DownloadStarting += (_, e) =>
		{
			_downloads++;
			_features.DownloadStarting(this, e);
			// A preview, or a tab a page opened, that never showed a document was only this download: it closes, as Chrome's
			// does, and the download goes on in the list. After the event, so the engine has the answer before the page goes.
			if (!_showedDocument && (_openedForPage || _features.InPreview(this))) Faults.Enqueue(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread(), () =>
			{
				if (IsOpen) _events.Closed();
			}, "preview download");
		};
		core.PermissionRequested += (_, e) => _features.PermissionRequested(this, e);
		core.ScriptDialogOpening += (_, e) => TestHooks.Guard(() => OnScriptDialog(e), "script dialog");
		core.ContainsFullScreenElementChanged += (_, _) => _features.FullScreenChanged(this);
		_viewers = new ViewerGuests(core, () => _features.FullScreenChanged(this), Background);
		_viewers.Start();
		// ActiveMatchIndex already counts from 1, as the find bar does; it is -1 with no match.
		core.Find.MatchCountChanged += (_, _) => _events.FindResult(core.Find.MatchCount, Math.Max(0, core.Find.ActiveMatchIndex));
		core.Find.ActiveMatchIndexChanged += (_, _) => _events.FindResult(core.Find.MatchCount, Math.Max(0, core.Find.ActiveMatchIndex));
	}

	private void FollowFrame(CoreWebView2Frame frame)
	{
		string? navigating = null;
		frame.NavigationStarting += (_, e) =>
		{
			navigating = e.Uri;
			_frames.Started(e.Uri);
		};
		frame.NavigationCompleted += (_, e) =>
		{
			if (e.IsSuccess && navigating is not null) _frames.Showed(navigating);
		};
		frame.FrameCreated += (_, e) => FollowFrame(e.Frame);
	}

	/// <summary>Whether a starting download is an embedded frame's navigation rather than the main frame's or a link's own `download`.</summary>
	public bool IsFrameDownload(string uri) => _frames.ClaimDownload(uri);

	/// <summary>Page.enable turns on the reason events; the frame tree says which frame is the main one, whose id stays for the page's life.</summary>
	private async Task ReadNavigationReasonsAsync(CoreWebView2 core)
	{
		await WhileOpenAsync(core.CallDevToolsProtocolMethodAsync("Page.enable", "{}").AsTask());
		using var tree = System.Text.Json.JsonDocument.Parse(await WhileOpenAsync(core.CallDevToolsProtocolMethodAsync("Page.getFrameTree", "{}").AsTask()));
		_mainFrameId = tree.RootElement.GetProperty("frameTree").GetProperty("frame").GetProperty("id").GetString();
	}

	private void NavigationRequested(string json)
	{
		using var request = System.Text.Json.JsonDocument.Parse(json);
		var root = request.RootElement;
		if (_mainFrameId is null || root.GetProperty("frameId").GetString() != _mainFrameId) return;
		// A link that opens a window or downloads raises no NavigationStarting here, so its reason would linger for that address.
		if (root.GetProperty("disposition").GetString() != "currentTab") return;
		_lastReason = (root.GetProperty("url").GetString()!, root.GetProperty("reason").GetString()!);
	}

	private async Task IconChangedAsync(CoreWebView2 core)
	{
		if (string.IsNullOrEmpty(core.FaviconUri))
		{
			_events.IconChanged(null, core.Source);
			return;
		}
		var source = core.Source;
		var icon = await SharpFavicon.ReadAsync((method, json) => WhileOpenAsync(core.CallDevToolsProtocolMethodAsync(method, json).AsTask()), core.FaviconUri)
			?? await IconDataUrlAsync(core);
		// The tab may have closed while the icon was read, or gone on to another address whose icon this may not be.
		if (IsOpen) _events.IconChanged(icon, core.Source == source ? source : null);
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

	private static readonly CoreWebView2WebResourceContext[] BlockableContexts =
	[
		CoreWebView2WebResourceContext.Script,
		CoreWebView2WebResourceContext.Image,
		CoreWebView2WebResourceContext.Stylesheet,
		CoreWebView2WebResourceContext.Font,
		CoreWebView2WebResourceContext.XmlHttpRequest,
		CoreWebView2WebResourceContext.Fetch,
		CoreWebView2WebResourceContext.Ping,
		CoreWebView2WebResourceContext.EventSource,
		CoreWebView2WebResourceContext.Other,
	];

	private static bool Refused(string request, string host, string page) =>
		TrackerRules.Blocks(request, page) || FilterRules.Bundled.Blocks(host, page);

	/// <summary>zing://home is generated by Zing; any other zing: page does not exist.</summary>
	/// <summary>
	/// Requests reach this on the UI thread while they wait. Whatever is not Zing's business (a subresource that is not a
	/// tracker's or a listed ad server's, a data: or blob: address) returns at once, after one parse and a few lookups.
	/// </summary>
	private void OnWebResourceRequested(CoreWebView2 sender, CoreWebView2WebResourceRequestedEventArgs e)
	{
		var request = e.Request.Uri;
		if (RelayedContent.IsRelayUrl(request))
		{
			ServeRelayed(sender, e);
			return;
		}
		if (request.StartsWith(StartPage.Scheme + "://", StringComparison.OrdinalIgnoreCase))
		{
			ServeStartPage(sender, e, request);
			return;
		}
		if (!Uri.TryCreate(request, UriKind.Absolute, out var address) || address.Scheme is not ("http" or "https")) return;
		// Before the http rule: view.zing is an https address, and a page must get its 404, never a request to the network.
		if (address.Scheme == Uri.UriSchemeHttps && string.Equals(address.Host, Zing.Core.Viewer.ViewerFiles.Host, StringComparison.OrdinalIgnoreCase))
		{
			e.Response = ViewerResponses.Make(sender.Environment, Zing.Core.Viewer.ViewerFiles.Unavailable());
			return;
		}
		var isDocument = e.ResourceContext == CoreWebView2WebResourceContext.Document;
		if (!isDocument) BlockableRequests++;
		var host = address.IdnHost;
		var listed = _features.BlockTrackers && (TrackerRules.IsTrackerHost(host) || FilterRules.Bundled.Names(host));
		if (!isDocument && !listed) return;
		// The page's own document is never a third party; a tracker's iframe or subresource is.
		var topDocument = isDocument && _approvedDocument is not null && RelayedNavigation.Same(_approvedDocument, request);
		if (!topDocument && listed && Refused(request, host, TopOf(sender)))
		{
			e.Response = sender.Environment.CreateWebResourceResponse(null, 403, "Forbidden", "");
			return;
		}
		if (!isDocument) return;
		var warned = ThreatGate.IsWithheld(_warnedDocument, request, isDocument);
		if (warned || !RelayedNavigation.MayRequestDocument(TopOf(sender), request, _approvedDocument)) e.Response = sender.Environment.CreateWebResourceResponse(null, 403, "Forbidden", "");
	}

	private void ServeStartPage(CoreWebView2 sender, CoreWebView2WebResourceRequestedEventArgs e, string request)
	{
		CoreWebView2WebResourceResponse response;
		try
		{
			var page = _serveStartPage(request);
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
	/// A relay-scheme request is served only for a relayed page's own files or the document Zing started; a web page's image,
	/// fetch or frame gets nothing, and never makes Zing fetch for it. Answered under a deferral, never by blocking this thread.
	/// </summary>
	private void ServeRelayed(CoreWebView2 sender, CoreWebView2WebResourceRequestedEventArgs e)
	{
		var isDocument = e.ResourceContext == CoreWebView2WebResourceContext.Document;
		// A relayed navigation that was sent to a warning is not fetched either, even from a relayed page.
		if (ThreatGate.IsWithheld(_warnedDocument, e.Request.Uri, isDocument) || !RelayedNavigation.MayServe(TopOf(sender), e.Request.Uri, isDocument, _approvedDocument))
		{
			e.Response = sender.Environment.CreateWebResourceResponse(null, 403, "Forbidden", "");
			return;
		}
		var deferral = e.GetDeferral();
		Background(ServeRelayedAsync(sender, e, deferral, e.Request.Method, e.Request.Uri, isDocument, _relayRuns.Current), "relayed page");
	}

	private async Task ServeRelayedAsync(CoreWebView2 sender, CoreWebView2WebResourceRequestedEventArgs e, Windows.Foundation.Deferral deferral, string method, string uri, bool isDocument, CancellationToken ct)
	{
		try
		{
			RelayedResponse served;
			try
			{
				served = await _relay.ServeAsync(method, uri, isDocument, ct);
			}
			catch (OperationCanceledException) when (ct.IsCancellationRequested)
			{
				// The page left, or closed: nothing is waiting for this file.
				if (IsOpen) e.Response = sender.Environment.CreateWebResourceResponse(null, 500, "Internal Server Error", "");
				return;
			}
			catch (Exception)
			{
				if (IsOpen) e.Response = sender.Environment.CreateWebResourceResponse(null, 500, "Internal Server Error", "");
				throw;
			}
			if (!IsOpen) return;
			var headers = string.Concat(served.Headers.Select(h => $"{h.Key}: {h.Value}\r\n"));
			e.Response = sender.Environment.CreateWebResourceResponse(new MemoryStream(served.Body).AsRandomAccessStream(), served.Status, StatusText(served.Status), headers);
		}
		finally
		{
			if (IsOpen) deferral.Complete();
		}
	}

	private static string StatusText(int status) => status switch
	{
		200 => "OK",
		302 => "Found",
		400 => "Bad Request",
		403 => "Forbidden",
		405 => "Method Not Allowed",
		502 => "Bad Gateway",
		_ => throw new ArgumentOutOfRangeException(nameof(status), status, "No reason phrase for this relay status"),
	};

	/// <summary>Plenty of script and no text means a web app that died on what the relay can't give it; say so instead of leaving a blank page.</summary>
	private async Task CheckForAppAsync(CoreWebView2 core, ulong navigation)
	{
		await Task.Delay(AppLikeWait);
		if (!IsOpen || _navigation != navigation) return;
		using var counts = System.Text.Json.JsonDocument.Parse(await WhileOpenAsync(core.ExecuteScriptAsync("[document.body ? document.body.innerText.length : 0, document.scripts.length]").AsTask()));
		var (text, scripts) = (counts.RootElement[0].GetInt32(), counts.RootElement[1].GetInt32());
		if (!RelayedContent.IsAppLikeFailure(text, scripts)) return;
		_events.Notice($"{new Uri(core.Source).Host} is a web app, and web apps can't work through the relay. Open it in a normal tab if you can reach it.");
	}

	/// <summary>
	/// The engine says when a renderer hangs but never when it recovers. So while it is hung, a trivial script is sent every few
	/// seconds, each a fresh chance to be answered; the first that comes back means the page works again and its mark goes.
	/// </summary>
	private async Task WatchForAnswerAsync(CoreWebView2 core)
	{
		while (IsOpen && _hung)
		{
			var ping = core.ExecuteScriptAsync("0").AsTask();
			Background(ping, "hung page ping");
			if (await Task.WhenAny(ping, Task.Delay(HangCheckEvery)) != ping) continue;
			if (!ping.IsCompletedSuccessfully || !_hung) return;
			_hung = false;
			_events.Responding();
			return;
		}
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
				// The dialog went with its renderer, which may never say so.
				_dialogOpen = false;
				// So did its requests.
				_uploads.Clear();
				_hung = false;
				// Nothing is left that beforeunload could keep; the window's dialogs of the dead document go.
				EndLeave(true);
				_features.DialogsGone(this);
				_events.Crashed(PageFault.Crashed);
				break;
			case CoreWebView2ProcessFailedKind.RenderProcessUnresponsive:
				_events.Crashed(PageFault.Unresponsive);
				if (_hung) break;
				_hung = true;
				Background(WatchForAnswerAsync(sender), "hung page check");
				break;
			case CoreWebView2ProcessFailedKind.BrowserProcessExited:
				EngineFailures.ReportBrowserLost(e.ExitCode);
				break;
		}
	}

	/// <summary>
	/// A popup must be the CoreWebView2 the engine asked for, not a fresh tab
	/// loading the same URL, or window.opener is null and OAuth popups (Google,
	/// Microsoft, payment pages) have nowhere to send their result.
	/// </summary>
	private void OnNewWindowRequested(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs e)
	{
		// First, so a test's one-shot modifiers go with this request even when it is refused.
		var modifiers = HeldModifiers();
		// A relayed page opens relayed windows only (an https address goes onto the relay, as its links do), and nothing else opens one
		// onto the relay; none opens onto Visit anyway.
		var target = RelayedNavigation.NewWindowTarget(TopOf(sender), e.Uri);
		if (target is null || !ThreatGate.NewWindowAllowed(e.Uri))
		{
			e.Handled = true;
			if (RelayedNavigation.PlainHttpNotice(TopOf(sender), e.Uri) is { } refused) _events.Notice(refused);
			return;
		}
		// WebView2 has no pop-up blocker of its own. A click counts for a few seconds (Chromium's transient activation), so a
		// sign-in pop-up opened after a short await still opens.
		if (!e.IsUserInitiated && !_features.PopupsAllowed(this))
		{
			e.Handled = true;
			_features.PopupBlocked(this, target);
			return;
		}
		var request = new PopupRequest(target, modifiers, IsPopup(e.WindowFeatures));
		Background(OpenPopupAsync(e, request), "popup");
	}

	/// <summary>
	/// Whether the page asked for a popup window rather than a tab: a size or a position, or the popup feature. WebView2 doesn't
	/// list popup itself; a window asked for as one has no toolbar (ShouldDisplayToolbar), which a plain window.open, a
	/// target=_blank link and noopener alone keep (link-preview e2e).
	/// </summary>
	private static bool IsPopup(CoreWebView2WindowFeatures features) => features.HasSize || features.HasPosition || !features.ShouldDisplayToolbar;

	/// <summary>
	/// The keys and button that asked for a window: WebView2 says nothing of them. Shift and Ctrl are read as the event fires,
	/// from the system (the input went to the engine's window, so this thread's key state can be stale); the middle button
	/// from the window's last middle press (HostWindow.MiddlePress, from Raw Input), used once. An e2e run never reads the real
	/// input (AGENTS.md): its tests set the modifiers first (set-modifiers), which win over everything.
	/// </summary>
	private Modifiers HeldModifiers()
	{
		var middle = _window.MiddlePress.Take(Environment.TickCount64);
		if (TestHooks.TakeModifiers() is { } set) return set;
		if (TestHooks.KeepsOutOfTheWay) return default;
		const int VK_SHIFT = 0x10, VK_CONTROL = 0x11;
		return new Modifiers(Native.IsHeldNow(VK_SHIFT), Native.IsHeldNow(VK_CONTROL), middle);
	}

	private async Task OpenPopupAsync(CoreWebView2NewWindowRequestedEventArgs e, PopupRequest request)
	{
		var target = request.Url;
		var deferral = e.GetDeferral();
		try
		{
			var popup = (WebView2Page)_events.Popup(request);
			// The engine starts the window on the address the page gave; for a mapped one that navigation is sent onto the relay by the rule for a relayed page's own.
			if (RelayedContent.IsRelayUrl(target)) popup.AllowFirstNavigation(target);
			await popup.Ready;
			// The opener or the popup may have closed while the engine started it: then there is no window to hand over.
			if (IsOpen && popup.IsOpen && popup.IsStarted)
			{
				e.NewWindow = popup.Core;
				popup._openedForPage = true;
				popup._events.WindowOpened();
			}
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
