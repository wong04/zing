namespace Zing.Host;

/// <summary>
/// e2e-only behaviour, switched on by ZING_E2E=1 (the WebView2 counterpart of
/// src/main/test-hooks.ts). Test runs must not disturb the person at the PC
/// (AGENTS.md): the window opens off-screen and out of the switchers, and
/// unhandled errors go to ZING_ERRORS_FILE, which the harness fails on.
/// </summary>
public static class TestHooks
{
	public static bool IsEndToEndRun => Environment.GetEnvironmentVariable("ZING_E2E") == "1";

	/// <summary>
	/// An e2e run's windows open off-screen, out of the switchers, and never take focus (AGENTS.md). The
	/// real-input tier (e2e/real-input.spec.ts, run by hand on a PC nobody is using) sets ZING_E2E_ONSCREEN=1:
	/// its window opens where a person's would and is activated, because OS input (SendInput) only reaches
	/// the window in front. Everything else about an e2e run stays: menus are recorded, the cursor never read.
	/// </summary>
	public static bool KeepsOutOfTheWay => IsEndToEndRun && Environment.GetEnvironmentVariable("ZING_E2E_ONSCREEN") != "1";

	/// <summary>Remote debugging port for Playwright; only honoured in e2e runs.</summary>
	public static string? DebuggingPort => IsEndToEndRun ? Environment.GetEnvironmentVariable("ZING_CDP_PORT") : null;

	/// <summary>Chromium switches for e2e runs: the debugging port, and with ZING_E2E_OFFLINE=1 a dead proxy so no test can reach a live site.</summary>
	public static string BrowserArguments()
	{
		if (!IsEndToEndRun) return "";
		var args = new List<string>();
		if (DebuggingPort is { } port) args.Add($"--remote-debugging-port={port}");
		// A granted camera or microphone gets the engine's fake devices, never the PC's own.
		args.Add("--use-fake-device-for-media-stream");
		if (Environment.GetEnvironmentVariable("ZING_E2E_OFFLINE") == "1") args.Add("--proxy-server=127.0.0.1:9 --proxy-bypass-list=<-loopback>");
		return string.Join(' ', args);
	}

	/// <summary>The app clock. ZING_TEST_CLOCK=1 in an e2e run gives one a test can move forward (test:advance-clock).</summary>
	public static Zing.Core.IClock Clock { get; } = IsEndToEndRun && Environment.GetEnvironmentVariable("ZING_TEST_CLOCK") == "1" ? new Zing.Core.OffsetClock() : new Zing.Core.SystemClock();

	/// <summary>
	/// In an e2e run, ZING_ENGINE_BASE_URL points the engine catalog at a fixture server on this PC, and
	/// ZING_ENGINE_CATALOG names the JSON file listing what it serves (the hashes depend on the files the test made).
	/// Null otherwise, and in a real run the pinned catalog is the only one.
	/// </summary>
	public static Zing.Core.Engines.EngineCatalog? EngineCatalog()
	{
		if (!IsEndToEndRun) return null;
		var baseUrl = Environment.GetEnvironmentVariable("ZING_ENGINE_BASE_URL");
		if (string.IsNullOrEmpty(baseUrl)) return null;
		var manifest = Environment.GetEnvironmentVariable("ZING_ENGINE_CATALOG") ?? throw new InvalidOperationException("ZING_ENGINE_BASE_URL needs ZING_ENGINE_CATALOG, the JSON file the fixture serves");
		return Zing.Core.Engines.EngineCatalog.FromFixture(new Uri(baseUrl), File.ReadAllText(manifest));
	}

	private static int? _closeNextAt;
	private static WindowScope? _newest;
	private static int _windows;
	private static bool _newestStarted;

	/// <summary>
	/// Test-only messages from Zing's own pages in an e2e run, answered by doing it, never by a
	/// reply the renderer's bridge would not expect: {kind: "test", action: "advance-clock", ms};
	/// "layers", "caption" and "live-counts" write JSON to <c>file</c>; "close-at" with <c>n</c> makes the next
	/// window close itself at its n-th scope checkpoint (WindowScope.Checkpoint), the teardown guard.
	/// </summary>
	public static bool TryHandle(string json, HostWindow window)
	{
		if (!IsEndToEndRun) return false;
		using var message = System.Text.Json.JsonDocument.Parse(json);
		var root = message.RootElement;
		if (!root.TryGetProperty("kind", out var kind) || kind.GetString() != "test") return false;
		var action = root.GetProperty("action").GetString();
		if (action == "layers")
		{
			File.WriteAllText(root.GetProperty("file").GetString()!, System.Text.Json.JsonSerializer.Serialize(Layers(window)));
			return true;
		}
		if (action == "caption")
		{
			var bar = window.AppWindow.TitleBar;
			File.WriteAllText(root.GetProperty("file").GetString()!, System.Text.Json.JsonSerializer.Serialize(new { background = bar.ButtonBackgroundColor, ink = bar.ButtonForegroundColor, height = bar.Height / window.Scale, buttonsHeight = bar.Height, buttonsWidth = bar.RightInset, passedBand = window.PassedThrough.Band, passedButtons = window.PassedThrough.Buttons }));
			return true;
		}
		if (action == "live-counts")
		{
			File.WriteAllText(root.GetProperty("file").GetString()!, System.Text.Json.JsonSerializer.Serialize(LiveCounts()));
			return true;
		}
		if (action == "add-tabs")
		{
			// Tab records without pages, so a test can have hundreds of tabs without hundreds of engines.
			var api = (Apis.TryGetValue(window, out var apiOf) ? apiOf() : null) ?? throw new InvalidOperationException("add-tabs needs a window with its chrome started");
			var count = root.GetProperty("count").GetInt32();
			var prefix = root.GetProperty("url").GetString()!;
			for (var i = 0; i < count; i++) api.Tabs.Create($"{prefix}{i}");
			return true;
		}
		if (action == "close-at")
		{
			var n = root.GetProperty("n").GetInt32();
			if (n < 1) throw new InvalidOperationException($"close-at counts checkpoints from 1, got {n}");
			_closeNextAt = n;
			return true;
		}
		if (action != "advance-clock") throw new InvalidOperationException($"Unknown test action {action}");
		if (Clock is not Zing.Core.OffsetClock clock) throw new InvalidOperationException("advance-clock needs ZING_TEST_CLOCK=1");
		clock.Advance(root.GetProperty("ms").GetInt64());
		return true;
	}

	/// <summary>
	/// What the window actually shows: each WebView2 with its address, whether it is visible, and its place
	/// in the window's content and size in DIPs. Page-side signals (document.visibilityState) don't follow a hidden view.
	/// </summary>
	private static List<object> Layers(HostWindow window) =>
		[.. window.Views.Select(v => new
		{
			url = v.Core.Source,
			visible = v.Visible,
			x = v.Bounds.X,
			y = v.Bounds.Y,
			width = v.Bounds.Width,
			height = v.Bounds.Height,
			depth = v.Depth,
		})];

	/// <summary>
	/// What is still alive, for the teardown guard: windows not yet destroyed, views not yet closed and
	/// views whose controller is still being created (every window), and the newest window's scope.
	/// </summary>
	private static object LiveCounts() => new
	{
		windows = HostWindow.OpenCount,
		views = HostedView.Live,
		pending = HostedView.Pending,
		captures = WebView2Page.CapturesInFlight,
		newest = new { serial = _windows, checkpoints = _newest?.Checkpoints ?? 0, started = _newestStarted, closed = _newest?.IsClosed ?? false },
	};

	/// <summary>Each window's chrome API, for test actions that change its tabs (add-tabs); e2e runs only.</summary>
	private static readonly Dictionary<HostWindow, Func<Zing.Core.ChromeApi?>> Apis = [];

	public static void Register(HostWindow window, Func<Zing.Core.ChromeApi?> api)
	{
		if (!IsEndToEndRun) return;
		Apis[window] = api;
		window.Destroyed += () => Apis.Remove(window);
	}

	/// <summary>A new window's scope, in an e2e run: counted, and closed at a checkpoint if a test asked (close-at).</summary>
	public static void Watch(WindowScope scope, Action closeWindow)
	{
		if (!IsEndToEndRun) return;
		_newest = scope;
		_windows++;
		_newestStarted = false;
		if (_closeNextAt is not { } n) return;
		_closeNextAt = null;
		scope.CloseAt(n, closeWindow);
	}

	/// <summary>A window finished starting (the end of MainWindow.StartAsync).</summary>
	public static void Started(WindowScope scope)
	{
		if (scope == _newest) _newestStarted = true;
	}

	/// <summary>
	/// e2e runs never show a menu: a Win32 menu appears on the person's screen even for the off-screen
	/// window (AGENTS.md). Its labels go to ZING_MENUS_FILE instead, one JSON array per menu, a separator as null.
	/// </summary>
	public static void RecordMenu(IReadOnlyList<Zing.Core.MenuItem> items)
	{
		var file = Environment.GetEnvironmentVariable("ZING_MENUS_FILE");
		if (string.IsNullOrEmpty(file)) throw new InvalidOperationException("e2e runs need ZING_MENUS_FILE to record menus");
		File.AppendAllText(file, System.Text.Json.JsonSerializer.Serialize(items.Select(i => i.Label)) + "\n");
	}

	/// <summary>
	/// e2e runs never open a dialog (AGENTS.md), Edge's print preview included. What would have opened goes to
	/// ZING_DIALOGS_FILE instead, one JSON object per line: its kind and the address of the page it was for.
	/// </summary>
	public static void RecordDialog(string kind, string url)
	{
		var file = Environment.GetEnvironmentVariable("ZING_DIALOGS_FILE");
		if (string.IsNullOrEmpty(file)) throw new InvalidOperationException("e2e runs need ZING_DIALOGS_FILE to record dialogs");
		File.AppendAllText(file, System.Text.Json.JsonSerializer.Serialize(new { kind, url }) + "\n");
	}

	/// <summary>
	/// Work nobody awaits: its failures are recorded, except a view that went with its closing window.
	/// This is also the host's error boundary for event handlers: a handler that awaits is a plain
	/// <c>void</c> that passes its async work here, never <c>async void</c>, which would crash the process on a throw.
	/// </summary>
	public static void Background(Task task, string what) =>
		_ = task.ContinueWith(t =>
		{
			if (t.Exception!.InnerExceptions.All(e => e is ViewGoneException)) return;
			Record(what, t.Exception);
		}, TaskContinuationOptions.OnlyOnFaulted);

	public static void RecordUnhandledErrors()
	{
		AppDomain.CurrentDomain.UnhandledException += (_, e) => Record("unhandled", e.ExceptionObject);
		TaskScheduler.UnobservedTaskException += (_, e) => Record("unobserved task", e.Exception);
	}

	/// <summary>Records a fault in the test runs' fault log (Faults), and in an e2e run also in ZING_ERRORS_FILE, which fails the test.</summary>
	public static void Record(string kind, object error)
	{
		Faults.Error(kind, error);
		var file = Environment.GetEnvironmentVariable("ZING_ERRORS_FILE");
		if (!IsEndToEndRun || string.IsNullOrEmpty(file)) return;
		File.AppendAllText(file, $"{kind}: {error}\n\n");
	}
}
