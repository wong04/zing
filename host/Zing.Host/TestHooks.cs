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

	/// <summary>Chromium switches for e2e runs: the debugging port, and with ZING_E2E_OFFLINE=1 a dead proxy so no test can reach a live site, and with ZING_E2E_NO_DNS=1 names that do not resolve.</summary>
	public static string BrowserArguments()
	{
		if (!IsEndToEndRun) return "";
		var args = new List<string>();
		if (DebuggingPort is { } port) args.Add($"--remote-debugging-port={port}");
		// A granted camera or microphone gets the engine's fake devices, never the PC's own.
		args.Add("--use-fake-device-for-media-stream");
		// With the updater on, a new profile blocks the engine's main thread for 5 s on most launches, and every e2e launch has a new profile.
		args.Add("--disable-component-update");
		// The engine refuses fullscreen to a page whose view hasn't the keyboard (SuppressFullscreenFromUnfocusedView), and a window kept
		// out of the way never gets it, so a test's requestFullscreen failed whenever another app was in front. A person's click focuses the view.
		if (KeepsOutOfTheWay) args.Add("--disable-features=SuppressFullscreenFromUnfocusedView");
		if (Environment.GetEnvironmentVariable("ZING_E2E_OFFLINE") == "1") args.Add("--proxy-server=127.0.0.1:9 --proxy-bypass-list=<-loopback>");
		// Every name fails to resolve (HostNameNotResolved, as a DNS block gives), without asking any DNS server; this machine's own addresses still work.
		if (Environment.GetEnvironmentVariable("ZING_E2E_NO_DNS") == "1") args.Add("--host-resolver-rules=\"MAP * ~NOTFOUND , EXCLUDE 127.0.0.1\"");
		return string.Join(' ', args);
	}

	/// <summary>
	/// What link inspection looks at in an e2e run: ZING_E2E_INSPECT_AS is JSON {"127.0.0.1:port": "a.stand-in.host"}, so a test's local server
	/// can stand for a flagged site without a real rule being weakened. Null outside an e2e run, and when it isn't set.
	/// </summary>
	public static Func<Uri, Uri>? InspectedAs()
	{
		if (!IsEndToEndRun || Environment.GetEnvironmentVariable("ZING_E2E_INSPECT_AS") is not { } json) return null;
		var standIns = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? throw new InvalidOperationException("ZING_E2E_INSPECT_AS must be a JSON object");
		return uri => standIns.TryGetValue(uri.Authority, out var host) ? new UriBuilder(uri) { Host = host, Port = -1 }.Uri : uri;
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

	private static readonly Queue<string[]> PickedFiles = new();

	/// <summary>
	/// What the file picker answers in an e2e run, which shows no dialog. A test queues each answer first with the test
	/// message {kind: "test", action: "pick-files", files: [...]}; an unqueued pick is an error, never a dialog.
	/// </summary>
	public static IReadOnlyList<string> TakePickedFiles()
	{
		lock (PickedFiles)
		{
			return PickedFiles.Count > 0 ? PickedFiles.Dequeue() : throw new InvalidOperationException("An e2e run shows no file dialog: queue the answer first with the pick-files test message");
		}
	}

	/// <summary>
	/// The network Diagnose sees in an e2e run, so no test reaches the internet: ZING_E2E_DOH_URL is a local DoH server that every
	/// provider is asked at, and ZING_E2E_PROBES is JSON {"system": [addresses], "tcp": bool, "tlsValid": bool} for what the probes find.
	/// Null outside an e2e run, and when neither is set.
	/// </summary>
	public static Zing.Core.Net.NetServices? NetServices()
	{
		if (!IsEndToEndRun) return null;
		var dohUrl = Environment.GetEnvironmentVariable("ZING_E2E_DOH_URL");
		var probes = Environment.GetEnvironmentVariable("ZING_E2E_PROBES");
		var relayUrl = Environment.GetEnvironmentVariable("ZING_E2E_RELAY_URL");
		// Relayed pages' network: a local server stands in for every site and for the relay and archive.
		if (relayUrl is not null)
			return new Zing.Core.Net.NetServices(FixedProbes.FromJson(probes ?? LocalRelayTransport.NoProbes), new Zing.Core.Net.DohClient(Zing.Core.Net.DohClient.NewHttpClient(), _ => dohUrl ?? "http://127.0.0.1:9/dns-query"), new LocalRelayTransport(new Uri(relayUrl)));
		if (dohUrl is null && probes is null) return null;
		if (dohUrl is null || probes is null) throw new InvalidOperationException("ZING_E2E_DOH_URL and ZING_E2E_PROBES go together");
		return new Zing.Core.Net.NetServices(FixedProbes.FromJson(probes), new Zing.Core.Net.DohClient(Zing.Core.Net.DohClient.NewHttpClient(), _ => dohUrl));
	}

	private static readonly StubDnsSystem Dns = new();

	/// <summary>
	/// What the app reads and changes DNS through in an e2e run: a stub that touches nothing (StubDnsSystem). The real
	/// DnsSetup, which would show a UAC prompt and change the machine's DNS, is never created in a test run. Null outside one.
	/// </summary>
	public static Zing.Core.Net.IDnsSystem? DnsSystem() => IsEndToEndRun ? Dns : null;

	private static readonly FakeHelloSealer Hello = new();

	/// <summary>What the vault asks for Windows Hello in an e2e run: a fake that never prompts, answering as "vault-hello" set it (FakeHelloSealer). Null outside one, so a real launch gets the real HelloSealer.</summary>
	public static FakeHelloSealer? HelloSealer() => IsEndToEndRun ? Hello : null;

	/// <summary>
	/// Sync in an e2e run, which never reaches Google or Railway: ZING_E2E_SYNC_SERVER is a local Zing.Server started with its fake
	/// issuer, and ZING_E2E_SYNC_SUB the Google sub to sign in as. Without them an e2e run has no sync, whatever the build has.
	/// With ZING_E2E_GOOGLE_AUTHORIZE and ZING_E2E_GOOGLE_TOKEN, a test's fake Google on this PC, the sign-in is the real one, in
	/// its window (SignInPopup); without them it's the fake issuer's, with no window (FakeIssuerSignIn).
	/// </summary>
	/// <param name="app">What the sign-in window opens over.</param>
	public static Zing.Core.Sync.SyncSetup? Sync(ZingApp app)
	{
		if (!IsEndToEndRun) throw new InvalidOperationException("TestHooks.Sync is for e2e runs only");
		if (Environment.GetEnvironmentVariable("ZING_E2E_SYNC_SERVER") is not { Length: > 0 } url) return null;
		var sub = Environment.GetEnvironmentVariable("ZING_E2E_SYNC_SUB") ?? throw new InvalidOperationException("ZING_E2E_SYNC_SERVER needs ZING_E2E_SYNC_SUB, the sub to sign in as");
		var server = new Uri(url);
		if (Environment.GetEnvironmentVariable("ZING_E2E_GOOGLE_AUTHORIZE") is not { Length: > 0 } authorize)
			return new Zing.Core.Sync.SyncSetup(server, new FakeIssuerSignIn(server, sub), Warn: Faults.Warning);
		var token = Environment.GetEnvironmentVariable("ZING_E2E_GOOGLE_TOKEN") ?? throw new InvalidOperationException("ZING_E2E_GOOGLE_AUTHORIZE needs ZING_E2E_GOOGLE_TOKEN, the fake token endpoint");
		var endpoints = new Zing.Core.Sync.GoogleEndpoints(OnThisPc(authorize), OnThisPc(token));
		return new Zing.Core.Sync.SyncSetup(server, new Zing.Core.Sync.GoogleSignIn("zing-e2e-client", "zing-e2e-secret", endpoints, address => SignInPopup.OpenOver(app, address)), Warn: Faults.Warning);
	}

	/// <summary>A fake Google endpoint, which must be on this PC: an e2e run never reaches Google.</summary>
	private static Uri OnThisPc(string address)
	{
		var uri = new Uri(address);
		if (!uri.IsLoopback) throw new InvalidOperationException($"An e2e run's Google endpoints must be on this PC, not {uri.Host}");
		return uri;
	}

	private static int? _closeNextAt;

	/// <summary>"exit-code-on-quit": the code Zing ends with once its last window is gone, as a crash during quit would; e2e/harness.spec.ts.</summary>
	public static int? QuitExitCode { get; private set; }
	private static WindowScope? _newest;
	private static int _windows;
	private static bool _newestStarted;

	/// <summary>
	/// Test-only messages from Zing's own pages in an e2e run, answered by doing it, never by a
	/// reply the renderer's bridge would not expect: {kind: "test", action: "advance-clock", ms};
	/// "vault-hello" with <c>result</c> pass, cancel or unavailable sets how the fake Windows Hello answers, and reset makes it forget the key it sealed with;
	/// "picker-key" with <c>vk</c> enters the account picker's key handler as a real key press on its page would;
	/// "preview-key" with <c>code</c> enters the window's key handler as that key pressed on the link preview's top page would;
	/// "page-messages" writes how many page messages were dropped to <c>file</c>;
	/// "set-modifiers" with <c>shift</c>, <c>ctrl</c> and <c>middle</c> sets the keys the next new-window request is taken to have been made with, once;
	/// "peek-state" writes the link preview's state to <c>file</c>; "click-scrim" sends the preview's scrim a left-button press, as a click on the dim would;
	/// "sign-in-window" writes the Google sign-in window's state to <c>file</c>, and with <c>close</c> closes it as the person would;
	/// "dns-stub" sets what the stub DNS lists and how its runs end; "layers", "autofill-settings", "caption" and "live-counts" write JSON to <c>file</c>; "close-at" with <c>n</c> makes the next
	/// window close itself at its n-th scope checkpoint (WindowScope.Checkpoint), the teardown guard.
	/// </summary>
	/// <summary>Written beside and moved into place: the test polls for the file and must never read half of it.</summary>
	private static void WriteWhole(string path, string json)
	{
		var part = path + ".part";
		File.WriteAllText(part, json);
		File.Move(part, path, overwrite: true);
	}

	public static bool TryHandle(string json, HostWindow window)
	{
		if (!IsEndToEndRun) return false;
		using var message = System.Text.Json.JsonDocument.Parse(json);
		var root = message.RootElement;
		if (!root.TryGetProperty("kind", out var kind) || kind.GetString() != "test") return false;
		var action = root.GetProperty("action").GetString();
		if (action == "pick-files")
		{
			var files = root.GetProperty("files").EnumerateArray().Select(f => f.GetString()!).ToArray();
			lock (PickedFiles) PickedFiles.Enqueue(files);
			return true;
		}
		if (action == "read-clipboard")
		{
			// The real clipboard is never opened in an e2e run (FileClipboard); this is the CF_HDROP block it would have been given, read back.
			var block = FileClipboard.Held ?? throw new InvalidOperationException("Nothing was copied to the clipboard");
			WriteWhole(root.GetProperty("file").GetString()!, System.Text.Json.JsonSerializer.Serialize(new { format = "CF_HDROP", paths = Zing.Core.DropFiles.Parse(block) }));
			return true;
		}
		if (action == "read-secret-clipboard")
		{
			// What a copied password put on the clipboard (the real clipboard is never opened in an e2e run), and the formats that keep it out of history and cloud sync.
			File.WriteAllText(root.GetProperty("file").GetString()!, System.Text.Json.JsonSerializer.Serialize(new { text = SecretClipboard.Held, formats = Zing.Core.Vault.SecretClipboardWriter.ExclusionFormats }));
			return true;
		}
		if (action == "set-modifiers")
		{
			lock (ModifiersGate) _modifiers = new Zing.Core.LinkPreview.Modifiers(root.GetProperty("shift").GetBoolean(), root.GetProperty("ctrl").GetBoolean(), root.GetProperty("middle").GetBoolean());
			return true;
		}
		if (action == "peek-state")
		{
			var api = (Apis.TryGetValue(window, out var peekApi) ? peekApi() : null) ?? throw new InvalidOperationException("peek-state needs a window with its chrome started");
			var preview = api.Tabs.Preview;
			WriteWhole(root.GetProperty("file").GetString()!, System.Text.Json.JsonSerializer.Serialize(new
			{
				open = preview is not null,
				layers = preview?.Layers.Select(l => new { url = l.Page.Url }).ToArray() ?? [],
				canExpand = preview?.CanExpand ?? false,
				sourceTabId = preview?.SourceTabId,
			}));
			return true;
		}
		if (action == "click-scrim")
		{
			var scrim = (Scrims.TryGetValue(window, out var scrimOf) ? scrimOf() : null) ?? throw new InvalidOperationException("click-scrim needs a window with a preview open");
			const uint WM_LBUTTONDOWN = 0x0201;
			Native.SendMessageW(scrim.Handle, WM_LBUTTONDOWN, 0, 0);
			return true;
		}
		if (action == "layers")
		{
			WriteWhole(root.GetProperty("file").GetString()!, System.Text.Json.JsonSerializer.Serialize(Layers(window)));
			return true;
		}
		if (action == "autofill-settings")
		{
			File.WriteAllText(root.GetProperty("file").GetString()!, System.Text.Json.JsonSerializer.Serialize(window.Views.Select(v => new { url = v.Core.Source, passwordAutosave = v.Core.Settings.IsPasswordAutosaveEnabled, generalAutofill = v.Core.Settings.IsGeneralAutofillEnabled })));
			return true;
		}
		if (action == "caption")
		{
			var bar = window.AppWindow.TitleBar;
			WriteWhole(root.GetProperty("file").GetString()!, System.Text.Json.JsonSerializer.Serialize(new { background = bar.ButtonBackgroundColor, ink = bar.ButtonForegroundColor, height = bar.Height / window.Scale, buttonsHeight = bar.Height, buttonsWidth = bar.RightInset, passedBand = window.PassedThrough.Band, passedButtons = window.PassedThrough.Buttons }));
			return true;
		}
		if (action == "live-counts")
		{
			WriteWhole(root.GetProperty("file").GetString()!, System.Text.Json.JsonSerializer.Serialize(LiveCounts()));
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
		if (action == "open-relayed")
		{
			var api = (Apis.TryGetValue(window, out var relayApi) ? relayApi() : null) ?? throw new InvalidOperationException("open-relayed needs a window with its chrome started");
			api.Tabs.OpenRelayed(root.GetProperty("url").GetString()!);
			return true;
		}
		if (action == "relay-consent")
		{
			var api = (Apis.TryGetValue(window, out var consentApi) ? consentApi() : null) ?? throw new InvalidOperationException("relay-consent needs a window with its chrome started");
			api.Consent.Allow();
			return true;
		}
		if (action == "picker-key")
		{
			var handler = PickerKeyHandlers.TryGetValue(window, out var keys) ? keys : throw new InvalidOperationException("picker-key needs a window with its chrome started");
			handler(root.GetProperty("vk").GetInt32());
			return true;
		}
		if (action == "preview-key")
		{
			var handler = PreviewKeyHandlers.TryGetValue(window, out var keys) ? keys : throw new InvalidOperationException("preview-key needs a window with its chrome started");
			handler(new Zing.Core.KeyInput(root.GetProperty("code").GetString()!, Ctrl: false, Shift: false, Alt: false));
			return true;
		}
		if (action == "page-messages")
		{
			File.WriteAllText(root.GetProperty("file").GetString()!, System.Text.Json.JsonSerializer.Serialize(new { dropped = Volatile.Read(ref _droppedPageMessages) }));
			return true;
		}
		if (action == "vault-hello")
		{
			Hello.Answer(root.GetProperty("result").GetString()!);
			return true;
		}
		if (action == "dns-stub")
		{
			Dns.Configure(root);
			return true;
		}
		if (action == "exit-code-on-quit")
		{
			QuitExitCode = root.GetProperty("code").GetInt32();
			return true;
		}
		if (action == "sign-in-window")
		{
			// The Google sign-in window: what it shows and where, written to file, then closed as the person would with close.
			var popup = SignInPopup.Current;
			WriteWhole(root.GetProperty("file").GetString()!, System.Text.Json.JsonSerializer.Serialize(popup is null ? new { open = false, ownedByThisWindow = false, address = "", width = 0.0, height = 0.0 } : new { open = true, ownedByThisWindow = popup.Owner == window.Hwnd, address = popup.Address, width = popup.Size.Width, height = popup.Size.Height }));
			if (root.TryGetProperty("close", out var close) && close.GetBoolean()) (popup ?? throw new InvalidOperationException("sign-in-window close needs the window open")).CloseAsThePersonWould();
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
	/// A link preview's scrim, while it exists, is one more entry with the address "scrim".
	/// </summary>
	private static List<object> Layers(HostWindow window)
	{
		List<object> layers = [.. window.Views.Select(v => new
		{
			url = v.Core.Source,
			visible = v.Visible,
			x = v.Bounds.X,
			y = v.Bounds.Y,
			width = v.Bounds.Width,
			height = v.Bounds.Height,
			depth = v.Depth,
			keyboard = v.HasKeyboard,
		})];
		if (!Scrims.TryGetValue(window, out var scrimOf) || scrimOf() is not { } scrim) return layers;
		var scale = window.Scale;
		var area = scrim.PageArea;
		layers.Add(new { url = "scrim", visible = scrim.Visible, x = area.X / scale, y = area.Y / scale, width = area.Width / scale, height = area.Height / scale, depth = scrim.Depth, keyboard = false });
		return layers;
	}

	/// <summary>Each window's preview scrim, for the layers hook; e2e runs only.</summary>
	private static readonly Dictionary<HostWindow, Func<PeekScrim?>> Scrims = [];

	public static void RegisterScrim(HostWindow window, Func<PeekScrim?> scrim)
	{
		if (!IsEndToEndRun) return;
		Scrims[window] = scrim;
		window.Destroyed += () => Scrims.Remove(window);
	}

	private static readonly object ModifiersGate = new();
	private static Zing.Core.LinkPreview.Modifiers? _modifiers;

	/// <summary>The modifiers a test set for the next new-window request (set-modifiers), once; null when none is set or outside an e2e run.</summary>
	public static Zing.Core.LinkPreview.Modifiers? TakeModifiers()
	{
		if (!IsEndToEndRun) return null;
		lock (ModifiersGate)
		{
			var set = _modifiers;
			_modifiers = null;
			return set;
		}
	}

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

	/// <summary>Each window's account-picker key handler (MainWindow.PickerKey), for picker-key; e2e runs only.</summary>
	private static readonly Dictionary<HostWindow, Func<int, bool>> PickerKeyHandlers = [];

	public static void RegisterPickerKeys(HostWindow window, Func<int, bool> handler)
	{
		if (!IsEndToEndRun) return;
		PickerKeyHandlers[window] = handler;
		window.Destroyed += () => PickerKeyHandlers.Remove(window);
	}

	/// <summary>Each window's key handler for its link preview's top page (MainWindow.PreviewKeyOnPage), for preview-key; e2e runs only.</summary>
	private static readonly Dictionary<HostWindow, Func<Zing.Core.KeyInput, bool>> PreviewKeyHandlers = [];

	public static void RegisterPreviewKeys(HostWindow window, Func<Zing.Core.KeyInput, bool> handler)
	{
		if (!IsEndToEndRun) return;
		PreviewKeyHandlers[window] = handler;
		window.Destroyed += () => PreviewKeyHandlers.Remove(window);
	}

	private static int _droppedPageMessages;

	/// <summary>A page message was dropped unread: too big, too deep, not JSON, or not a shape the autofill script sends (Ruling 29).</summary>
	public static void PageMessageDropped() => Interlocked.Increment(ref _droppedPageMessages);

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

	/// <summary>
	/// The same boundary for synchronous work the UI thread runs on its own: a timer, a queued action, a window event, a
	/// menu click. An exception out of a dispatcher callback or the window procedure ends the process, so nothing rethrows:
	/// a view that went with its closing window is expected, and everything else is recorded.
	/// </summary>
	public static void Guard(Action action, string what)
	{
		try
		{
			action();
		}
		catch (ViewGoneException)
		{
			// Expected, as in Background: the window closed while the work was queued.
		}
		catch (Exception error)
		{
			Record(what, error);
		}
	}

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
		// Called from thread-pool continuations too: two appends at once would hit a sharing violation.
		lock (RecordGate) File.AppendAllText(file, $"{kind}: {error}\n\n");
	}

	private static readonly object RecordGate = new();
}
