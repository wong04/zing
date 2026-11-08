using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

// Throwaway spike for docs/superpowers/specs/2026-11-08-link-preview-design.md. Runs four checks on its
// own and writes results.json (plus screenshots) next to the exe, then exits:
//   C. Can a link click be told from other navigations (Page.frameRequestedNavigation's reason) in time to cancel it?
//   D. Can a new-window request be held unshown until its first real document, keeping window.opener?
//   A. Does a layered owned popup with a SetWindowRgn hole dim a live child-window page and pass clicks through the hole?
//   B. Does "expand" keep the same CoreWebView2 with no reload: moving the child window, then re-parenting the controller?
// Pages are served from one local server on two hosts (127.0.0.1 and localhost), so cross-host is real.
public static class Program
{
	[STAThread]
	public static void Main(string[] args)
	{
		Native.SetProcessDpiAwarenessContext(-4);
		WinRT.ComWrappersSupport.InitializeComWrappers();
		var dispatcher = Microsoft.UI.Dispatching.DispatcherQueueController.CreateOnCurrentThread();
		SynchronizationContext.SetSynchronizationContext(new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(dispatcher.DispatcherQueue));
		var spike = new Spike(stay: args.Contains("--stay"));
		_ = spike.RunAsync().ContinueWith(t =>
		{
			File.WriteAllText(Path.Combine(Spike.Out, "error.txt"), t.Exception!.ToString());
			Native.PostQuitMessage(1);
		}, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.FromCurrentSynchronizationContext());
		while (Native.GetMessageW(out var msg, 0, 0, 0) > 0)
		{
			Native.TranslateMessage(ref msg);
			Native.DispatchMessageW(ref msg);
		}
		GC.KeepAlive(dispatcher);
	}
}

public sealed class Spike
{
	public static readonly string Out = AppContext.BaseDirectory;
	private const byte ScrimAlpha = 115;

	private readonly bool _stay;
	private readonly Native.WndProc _proc;
	private readonly nint _main;
	private readonly Dictionary<string, object?> _results = new();
	private readonly List<string> _trace = new();
	private int _seq;
	private int _port;
	private nint _scrim;
	private Action? _relayout;

	public Spike(bool stay)
	{
		_stay = stay;
		_proc = WndProc;
		_main = Win.CreateMain(_proc, "Link preview spike");
	}

	private string A(string path) => $"http://127.0.0.1:{_port}{path}";
	private string B(string path) => $"http://localhost:{_port}{path}";

	public async Task RunAsync()
	{
		_port = Server.Start(this);
		var data = Environment.GetEnvironmentVariable("ZING_USER_DATA") ?? Path.Combine(Path.GetTempPath(), $"zing-link-preview-spike-{Environment.ProcessId}");
		var env = await CoreWebView2Environment.CreateWithOptionsAsync(null, data, new CoreWebView2EnvironmentOptions());

		var tabHost = Win.CreateChild(_main);
		var tab = await NewController(env, tabHost);
		_relayout = () => Place(tabHost, tab, PageRect());
		_relayout();
		await Load(tab, A("/tab"));

		await CheckClicks(tab);
		await CheckHeldWindows(env, tab);
		var (previewHost, preview) = await CheckScrim(env, tabHost, tab);
		await CheckExpand(tabHost, tab, previewHost, preview);

		_results["trace"] = _trace;
		File.WriteAllText(Path.Combine(Out, "results.json"), JsonSerializer.Serialize(_results, new JsonSerializerOptions { WriteIndented = true }));
		if (!_stay) Native.PostQuitMessage(0);
	}

	// ---- C. link-click detection -------------------------------------------------------------

	private async Task CheckClicks(CoreWebView2Controller tab)
	{
		var core = tab.CoreWebView2;
		var reasons = new List<(int seq, string url, string reason)>();
		var reasonArrived = new Dictionary<string, TaskCompletionSource<string>>();
		await core.CallDevToolsProtocolMethodAsync("Page.enable", "{}");
		core.GetDevToolsProtocolEventReceiver("Page.frameRequestedNavigation").DevToolsProtocolEventReceived += (_, e) =>
		{
			using var doc = JsonDocument.Parse(e.ParameterObjectAsJson);
			var url = doc.RootElement.GetProperty("url").GetString()!;
			var reason = doc.RootElement.GetProperty("reason").GetString()!;
			var seq = ++_seq;
			reasons.Add((seq, url, reason));
			Trace($"FRN #{seq} {reason} {url}");
			Waiter(reasonArrived, url).TrySetResult(reason);
		};

		var outcomes = new List<object>();
		core.NavigationStarting += async (_, e) =>
		{
			var seq = ++_seq;
			var host = new Uri(e.Uri).Host;
			Trace($"NavStarting #{seq} user={e.IsUserInitiated} redirect={e.IsRedirected} {e.Uri}");
			if (host != "localhost") return;
			// Cross-host: was the reason already known? (NavigationStarting has no deferral in this projection.)
			var before = reasons.LastOrDefault(r => r.url == e.Uri);
			e.Cancel = true;
			var waiter = Waiter(reasonArrived, e.Uri).Task;
			string? arrivedLater = before.reason is null && await Task.WhenAny(waiter, Task.Delay(300)) == waiter ? waiter.Result : null;
			outcomes.Add(new { e.Uri, e.IsUserInitiated, e.IsRedirected, reasonBeforeNavStarting = before.reason, reasonBeforeSeq = before.reason is null ? (int?)null : before.seq, navStartingSeq = seq, reasonArrivedAfter = arrivedLater });
		};

		var cases = new (string name, Func<Task> act)[]
		{
			("real anchor click", () => ClickElement(core, "#cross")),
			("real click, same-host link that 302s to the other host", () => ClickElement(core, "#redirect")),
			("script a.click()", () => Exec(core, "document.querySelector('#cross').click()")),
			("script location.href", () => Exec(core, $"location.href = '{B("/preview?script")}'")),
			("real click on a form POST button", () => ClickElement(core, "#postbtn")),
		};
		var results = new Dictionary<string, object?>();
		foreach (var (name, act) in cases)
		{
			var start = outcomes.Count;
			await act();
			await Task.Delay(800);
			results[name] = outcomes.Skip(start).ToList();
			if (core.Source != A("/tab")) await Load(tab, A("/tab"));
		}
		results["stillOnTab"] = core.Source;
		_results["C_clicks"] = results;
	}

	private static TaskCompletionSource<string> Waiter(Dictionary<string, TaskCompletionSource<string>> map, string url)
	{
		if (!map.TryGetValue(url, out var tcs) || tcs.Task.IsCompleted) map[url] = tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
		return tcs;
	}

	// ---- D. held new windows --------------------------------------------------------------------

	private async Task CheckHeldWindows(CoreWebView2Environment env, CoreWebView2Controller tab)
	{
		var core = tab.CoreWebView2;
		var held = new List<Held>();
		core.NewWindowRequested += async (_, e) =>
		{
			var deferral = e.GetDeferral();
			var hwnd = Win.CreateChild(_main, visible: false);
			var controller = await NewController(env, hwnd);
			var item = new Held(e.Uri, e.IsUserInitiated, hwnd, controller);
			held.Add(item);
			controller.CoreWebView2.NavigationStarting += (_, n) =>
			{
				item.Starts.Add(n.Uri);
				if (item.FirstReal is null && n.Uri.StartsWith("http")) item.FirstReal = n.Uri;
			};
			e.NewWindow = controller.CoreWebView2;
			e.Handled = true;
			deferral.Complete();
			Trace($"NewWindowRequested {e.Uri} user={e.IsUserInitiated}");
		};

		var cases = new (string name, string selector)[]
		{
			("target=_blank link", "#blank"),
			("window.open('about:blank') then set location", "#openblank"),
			("window.open(url)", "#openurl"),
		};
		var results = new Dictionary<string, object?>();
		foreach (var (name, selector) in cases)
		{
			var start = held.Count;
			await ClickElement(core, selector);
			await Task.Delay(1500);
			if (held.Count == start)
			{
				results[name] = "no NewWindowRequested";
				continue;
			}
			var item = held[^1];
			var visibleWhileHeld = Native.IsWindowVisible(item.Hwnd);
			// Decision made: show it as a preview.
			Place(item.Hwnd, item.Controller, PreviewRect());
			Native.ShowWindow(item.Hwnd, 5);
			await Task.Delay(300);
			var opener = await Exec(item.Controller.CoreWebView2, "String(window.opener !== null)");
			var openerTicksBefore = await Exec(core, "String(window.fromPopup || 0)");
			await Exec(item.Controller.CoreWebView2, "window.opener && window.opener.postMessage('hi', '*'); 'sent'");
			await Task.Delay(200);
			var openerTicksAfter = await Exec(core, "String(window.fromPopup || 0)");
			results[name] = new { requestUri = item.RequestUri, item.UserInitiated, firstRealDocument = item.FirstReal, navigationStarts = item.Starts, visibleWhileHeld, hasOpener = opener, openerPostMessage = $"{openerTicksBefore} -> {openerTicksAfter}", finalSource = item.Controller.CoreWebView2.Source };
			item.Controller.Close();
			Native.DestroyWindow(item.Hwnd);
		}
		_results["D_heldWindows"] = results;
	}

	private sealed class Held(string requestUri, bool userInitiated, nint hwnd, CoreWebView2Controller controller)
	{
		public string RequestUri { get; } = requestUri;
		public bool UserInitiated { get; } = userInitiated;
		public nint Hwnd { get; } = hwnd;
		public CoreWebView2Controller Controller { get; } = controller;
		public List<string> Starts { get; } = new();
		public string? FirstReal { get; set; }
	}

	// ---- A. scrim with a hole -------------------------------------------------------------------

	private async Task<(nint, CoreWebView2Controller)> CheckScrim(CoreWebView2Environment env, nint tabHost, CoreWebView2Controller tab)
	{
		var previewHost = Win.CreateChild(_main);
		var preview = await NewController(env, previewHost);
		await Load(preview, B("/preview"));
		_scrim = Win.CreateScrim(_main, ScrimAlpha);
		_relayout = () =>
		{
			Place(tabHost, tab, PageRect());
			LayoutPreview(previewHost, preview);
		};
		LayoutPreview(previewHost, preview);
		Native.SetForegroundWindow(_main);
		await Task.Delay(700);

		var first = await SampleScrim(tab, previewHost, "a-scrim-1");
		Native.SetWindowPos(_main, 0, 0, 0, 1500, 950, 0x16); // SWP_NOMOVE|SWP_NOZORDER|SWP_NOACTIVATE
		await Task.Delay(700);
		var resized = await SampleScrim(tab, previewHost, "a-scrim-2-resized");
		_results["A_scrim"] = new { first, resized };
		return (previewHost, preview);
	}

	private void LayoutPreview(nint previewHost, CoreWebView2Controller preview)
	{
		var rect = PreviewRect();
		Place(previewHost, preview, rect);
		Native.SetWindowPos(previewHost, 0, 0, 0, 0, 0, 0x13); // HWND_TOP, NOSIZE|NOMOVE|NOACTIVATE
		Win.PlaceScrim(_scrim, _main, PageRect(), rect);
	}

	private async Task<object> SampleScrim(CoreWebView2Controller tab, nint previewHost, string shot)
	{
		var page = ToScreen(PageRect());
		var hole = ToScreen(PreviewRect());
		var holeCentre = new Native.POINT { X = (hole.Left + hole.Right) / 2, Y = (hole.Top + hole.Bottom) / 2 };
		var dimPoint = new Native.POINT { X = page.Left + 10, Y = (page.Top + page.Bottom) / 2 };
		var tick1 = await Exec(tab.CoreWebView2, "String(window.ticks)");
		await Task.Delay(400);
		var tick2 = await Exec(tab.CoreWebView2, "String(window.ticks)");
		Screen.SaveBmp(Path.Combine(Out, shot + ".bmp"), Native.WindowRect(_main));
		return new
		{
			holePixel = Screen.Pixel(holeCentre),
			dimPixel = Screen.Pixel(dimPoint),
			expectedDim = $"#00{(int)(160 * (1 - ScrimAlpha / 255.0)):X2}00 (page #00A000 under {ScrimAlpha}/255 black)",
			hitInHole = Win.Describe(Native.WindowFromPoint(holeCentre), previewHost),
			hitInHoleIsPreview = Win.IsSelfOrChild(previewHost, Native.WindowFromPoint(holeCentre)),
			hitOnDimIsScrim = Native.WindowFromPoint(dimPoint) == _scrim,
			pageBehindLive = $"{tick1} -> {tick2}",
		};
	}

	// ---- B. expand without reload ---------------------------------------------------------------

	private async Task CheckExpand(nint tabHost, CoreWebView2Controller tab, nint previewHost, CoreWebView2Controller preview)
	{
		var core = preview.CoreWebView2;
		var starts = 0;
		core.NavigationStarting += (_, _) => starts++;
		await Exec(core, "window.__marker = 'kept'; document.querySelector('input').value = 'typed text'; scrollTo(0, 700); 'ok'");
		var before = await State(core);

		// (1) Design A: the preview is already a child page; expanding moves its window into the tab slot.
		Native.DestroyWindow(_scrim);
		_scrim = 0;
		Native.ShowWindow(tabHost, 0);
		_relayout = () => Place(previewHost, preview, PageRect());
		_relayout();
		await Task.Delay(600);
		var afterMove = await State(core);
		var centre = ToScreen(PageRect());
		Screen.SaveBmp(Path.Combine(Out, "b-expanded.bmp"), Native.WindowRect(_main));
		var pixelAfterMove = Screen.Pixel(new Native.POINT { X = (centre.Left + centre.Right) / 2, Y = (centre.Top + centre.Bottom) / 2 });

		// (2) Design B's need: re-parent the controller into another window (the old tab's host).
		tab.Close();
		Native.ShowWindow(tabHost, 5);
		preview.ParentWindow = CoreWebView2ControllerWindowReference.CreateFromWindowHandle((ulong)tabHost);
		Native.ShowWindow(previewHost, 0);
		_relayout = () => Place(tabHost, preview, PageRect());
		_relayout();
		preview.NotifyParentWindowPositionChanged();
		await Task.Delay(600);
		var afterReparent = await State(core);
		var pixelAfterReparent = Screen.Pixel(new Native.POINT { X = (centre.Left + centre.Right) / 2, Y = (centre.Top + centre.Bottom) / 2 });

		_results["B_expand"] = new { before, afterMove, pixelAfterMove, afterReparent, pixelAfterReparent, navigationStartsDuringExpand = starts };
	}

	private static async Task<object> State(CoreWebView2 core) => JsonSerializer.Deserialize<JsonElement>(
		JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync("JSON.stringify({ marker: window.__marker, input: document.querySelector('input').value, scrollY, timeOrigin: performance.timeOrigin, url: location.href })"))!);

	// ---- helpers --------------------------------------------------------------------------------

	private static async Task<CoreWebView2Controller> NewController(CoreWebView2Environment env, nint hwnd) =>
		await env.CreateCoreWebView2ControllerAsync(CoreWebView2ControllerWindowReference.CreateFromWindowHandle((ulong)hwnd));

	private static async Task Load(CoreWebView2Controller controller, string url)
	{
		var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		void Completed(object? s, CoreWebView2NavigationCompletedEventArgs e) => done.TrySetResult();
		controller.CoreWebView2.NavigationCompleted += Completed;
		controller.CoreWebView2.Navigate(url);
		await done.Task;
		controller.CoreWebView2.NavigationCompleted -= Completed;
	}

	private static async Task<string> Exec(CoreWebView2 core, string script)
	{
		var json = await core.ExecuteScriptAsync(script);
		return json.StartsWith('"') ? JsonSerializer.Deserialize<string>(json)! : json;
	}

	// A trusted click (user gesture) through DevTools input, without touching the real mouse.
	private static async Task ClickElement(CoreWebView2 core, string selector)
	{
		var rect = await Exec(core, $"(() => {{ const r = document.querySelector('{selector}').getBoundingClientRect(); return JSON.stringify([r.x + r.width / 2, r.y + r.height / 2]) }})()");
		var xy = JsonSerializer.Deserialize<double[]>(rect)!;
		foreach (var type in new[] { "mousePressed", "mouseReleased" })
			await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", JsonSerializer.Serialize(new { type, x = xy[0], y = xy[1], button = "left", clickCount = 1 }));
	}

	private static void Place(nint hwnd, CoreWebView2Controller controller, Native.RECT r)
	{
		if (hwnd != 0) Native.SetWindowPos(hwnd, 0, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, 0x14);
		controller.Bounds = new Windows.Foundation.Rect(0, 0, r.Right - r.Left, r.Bottom - r.Top);
	}

	private Native.RECT PageRect()
	{
		Native.GetClientRect(_main, out var c);
		var top = (int)(48 * Native.GetDpiForWindow(_main) / 96.0); // stand-in for the toolbar: not dimmed
		return new Native.RECT { Left = 0, Top = top, Right = c.Right, Bottom = c.Bottom };
	}

	private Native.RECT PreviewRect()
	{
		var p = PageRect();
		var scale = Native.GetDpiForWindow(_main) / 96.0;
		var side = Math.Max((int)((p.Right - p.Left) * 0.06), (int)(48 * scale));
		var v = (int)(24 * scale);
		return new Native.RECT { Left = p.Left + side, Top = p.Top + v, Right = p.Right - side, Bottom = p.Bottom - v };
	}

	private Native.RECT ToScreen(Native.RECT r)
	{
		var o = new Native.POINT();
		Native.ClientToScreen(_main, ref o);
		return new Native.RECT { Left = r.Left + o.X, Top = r.Top + o.Y, Right = r.Right + o.X, Bottom = r.Bottom + o.Y };
	}

	private nint WndProc(nint hwnd, uint msg, nint w, nint l)
	{
		switch (msg)
		{
			case 0x0005: // WM_SIZE
			case 0x0003: // WM_MOVE
				_relayout?.Invoke();
				break;
			case 0x02E0: // WM_DPICHANGED
				var r = Marshal.PtrToStructure<Native.RECT>(l);
				Native.SetWindowPos(hwnd, 0, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, 0x14);
				return 0;
			case 0x0002: // WM_DESTROY
				Native.PostQuitMessage(0);
				return 0;
		}
		return Native.DefWindowProcW(hwnd, msg, w, l);
	}

	public void Trace(string line) => _trace.Add($"{DateTime.Now:HH:mm:ss.fff} {line}");

	public string Page(string path, string query) => path switch
	{
		"/tab" => $$"""
			<!doctype html><title>Tab</title>
			<body style="margin:0;min-height:100vh;background:#00A000;font:18px system-ui;padding:16px;box-sizing:border-box">
			<p><a id=cross href="{{B("/preview")}}">cross-host link</a></p>
			<p><a id=redirect href="{{A("/redirect")}}">same-host link that redirects</a></p>
			<p><a id=blank target=_blank href="{{B("/opened")}}">target=_blank</a></p>
			<p><button id=openblank onclick="const w = window.open('about:blank'); setTimeout(() => w.location = '{{B("/opened?late")}}', 50)">open blank then set</button></p>
			<p><button id=openurl onclick="window.open('{{B("/opened?direct")}}')">window.open(url)</button></p>
			<form method=post action="{{B("/posted")}}"><button id=postbtn>form post</button></form>
			<script>addEventListener("message", () => window.fromPopup = (window.fromPopup || 0) + 1); window.ticks = 0; setInterval(() => window.ticks++, 50)</script>
			""",
		"/preview" => """
			<!doctype html><title>Preview</title>
			<body style="margin:0;background:#DC0000;font:18px system-ui;padding:16px">
			<input placeholder="type here"><div style="height:3000px">tall page</div>
			""",
		"/opened" => """<!doctype html><title>Opened</title><body style="background:#0040DC;color:white">opened</body>""",
		_ => """<!doctype html><title>Other</title><body>other</body>""",
	};
}

public static class Server
{
	public static int Start(Spike spike)
	{
		var port = FreePort();
		var listener = new HttpListener();
		listener.Prefixes.Add($"http://127.0.0.1:{port}/");
		listener.Prefixes.Add($"http://localhost:{port}/");
		listener.Start();
		_ = Task.Run(async () =>
		{
			while (listener.IsListening)
			{
				var ctx = await listener.GetContextAsync();
				var path = ctx.Request.Url!.AbsolutePath;
				if (path == "/redirect")
				{
					ctx.Response.Redirect($"http://localhost:{port}/preview?redirected");
					ctx.Response.Close();
					continue;
				}
				var body = Encoding.UTF8.GetBytes(spike.Page(path, ctx.Request.Url.Query));
				ctx.Response.ContentType = "text/html; charset=utf-8";
				await ctx.Response.OutputStream.WriteAsync(body);
				ctx.Response.Close();
			}
		});
		return port;
	}

	private static int FreePort()
	{
		var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
		l.Start();
		var port = ((IPEndPoint)l.LocalEndpoint).Port;
		l.Stop();
		return port;
	}
}

public static class Win
{
	private static readonly Native.WndProc ChildProc = (h, m, w, l) => Native.DefWindowProcW(h, m, w, l);
	private static readonly Native.WndProc ScrimProc = (h, m, w, l) =>
	{
		if (m == 0x0021) return 3; // WM_MOUSEACTIVATE -> MA_NOACTIVATE
		if (m == 0x0201) File.AppendAllText(Path.Combine(Spike.Out, "scrim-clicks.txt"), "click\n"); // WM_LBUTTONDOWN: would close the preview
		return Native.DefWindowProcW(h, m, w, l);
	};
	private static bool _registered;

	private static void Register()
	{
		if (_registered) return;
		var inst = Native.GetModuleHandleW(null);
		foreach (var (name, proc, brush) in new[] { ("SpikeChild", ChildProc, 0), ("SpikeScrim", ScrimProc, 4 /* BLACK_BRUSH */) })
		{
			var wc = new Native.WNDCLASSEXW { cbSize = (uint)Marshal.SizeOf<Native.WNDCLASSEXW>(), lpfnWndProc = Marshal.GetFunctionPointerForDelegate(proc), hInstance = inst, hCursor = Native.LoadCursorW(0, 32512), hbrBackground = brush == 0 ? 0 : Native.GetStockObject(brush), lpszClassName = name };
			if (Native.RegisterClassExW(ref wc) == 0) throw new InvalidOperationException($"RegisterClassEx {name} failed: {Marshal.GetLastWin32Error()}");
		}
		_registered = true;
	}

	public static nint CreateMain(Native.WndProc proc, string title)
	{
		var wc = new Native.WNDCLASSEXW { cbSize = (uint)Marshal.SizeOf<Native.WNDCLASSEXW>(), lpfnWndProc = Marshal.GetFunctionPointerForDelegate(proc), hInstance = Native.GetModuleHandleW(null), hCursor = Native.LoadCursorW(0, 32512), hbrBackground = Native.GetStockObject(0), lpszClassName = "SpikeMain" };
		if (Native.RegisterClassExW(ref wc) == 0) throw new InvalidOperationException($"RegisterClassEx failed: {Marshal.GetLastWin32Error()}");
		var hwnd = Native.CreateWindowExW(0, "SpikeMain", title, 0x00CF0000 | 0x02000000, 120, 120, 1200, 800, 0, 0, wc.hInstance, 0);
		if (hwnd == 0) throw new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastWin32Error()}");
		Native.ShowWindow(hwnd, 5);
		return hwnd;
	}

	public static nint CreateChild(nint parent, bool visible = true)
	{
		Register();
		const uint WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_CLIPSIBLINGS = 0x04000000, WS_CLIPCHILDREN = 0x02000000;
		var hwnd = Native.CreateWindowExW(0, "SpikeChild", "page", WS_CHILD | WS_CLIPSIBLINGS | WS_CLIPCHILDREN | (visible ? WS_VISIBLE : 0), 0, 0, 10, 10, parent, 0, Native.GetModuleHandleW(null), 0);
		if (hwnd == 0) throw new InvalidOperationException($"CreateWindowEx child failed: {Marshal.GetLastWin32Error()}");
		return hwnd;
	}

	public static nint CreateScrim(nint owner, byte alpha)
	{
		Register();
		const uint WS_POPUP = 0x80000000, WS_VISIBLE = 0x10000000;
		const uint WS_EX_LAYERED = 0x80000, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
		var hwnd = Native.CreateWindowExW(WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, "SpikeScrim", "scrim", WS_POPUP, 0, 0, 10, 10, owner, 0, Native.GetModuleHandleW(null), 0);
		if (hwnd == 0) throw new InvalidOperationException($"CreateWindowEx scrim failed: {Marshal.GetLastWin32Error()}");
		if (!Native.SetLayeredWindowAttributes(hwnd, 0, alpha, 2 /* LWA_ALPHA */)) throw new InvalidOperationException($"SetLayeredWindowAttributes failed: {Marshal.GetLastWin32Error()}");
		Native.ShowWindow(hwnd, 4); // SW_SHOWNOACTIVATE
		return hwnd;
	}

	// page and hole are client coordinates of the owner; the region is relative to the scrim.
	public static void PlaceScrim(nint scrim, nint owner, Native.RECT page, Native.RECT hole)
	{
		if (scrim == 0) return;
		var o = new Native.POINT();
		Native.ClientToScreen(owner, ref o);
		int w = page.Right - page.Left, h = page.Bottom - page.Top;
		Native.SetWindowPos(scrim, 0, page.Left + o.X, page.Top + o.Y, w, h, 0x14);
		var all = Native.CreateRectRgn(0, 0, w, h);
		var cut = Native.CreateRectRgn(hole.Left - page.Left, hole.Top - page.Top, hole.Right - page.Left, hole.Bottom - page.Top);
		Native.CombineRgn(all, all, cut, 4 /* RGN_DIFF */);
		Native.DeleteObject(cut);
		if (Native.SetWindowRgn(scrim, all, true) == 0) throw new InvalidOperationException("SetWindowRgn failed");
	}

	public static bool IsSelfOrChild(nint parent, nint hwnd) => hwnd == parent || Native.IsChild(parent, hwnd);

	public static string Describe(nint hwnd, nint previewHost)
	{
		var sb = new StringBuilder(256);
		Native.GetClassNameW(hwnd, sb, sb.Capacity);
		return $"{sb} (inside preview host: {IsSelfOrChild(previewHost, hwnd)})";
	}
}

public static class Screen
{
	public static string Pixel(Native.POINT p)
	{
		var dc = Native.GetDC(0);
		var c = Native.GetPixel(dc, p.X, p.Y);
		Native.ReleaseDC(0, dc);
		return $"#{c & 0xFF:X2}{(c >> 8) & 0xFF:X2}{(c >> 16) & 0xFF:X2}";
	}

	public static void SaveBmp(string path, Native.RECT r)
	{
		int w = r.Right - r.Left, h = r.Bottom - r.Top;
		var screen = Native.GetDC(0);
		var mem = Native.CreateCompatibleDC(screen);
		var bmp = Native.CreateCompatibleBitmap(screen, w, h);
		var old = Native.SelectObject(mem, bmp);
		Native.BitBlt(mem, 0, 0, w, h, screen, r.Left, r.Top, 0x00CC0020 | 0x40000000); // SRCCOPY | CAPTUREBLT (layered windows)
		var info = new Native.BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = h, biPlanes = 1, biBitCount = 24 };
		var stride = (w * 3 + 3) & ~3;
		var pixels = new byte[stride * h];
		Native.GetDIBits(mem, bmp, 0, (uint)h, pixels, ref info, 0);
		Native.SelectObject(mem, old);
		Native.DeleteObject(bmp);
		Native.DeleteDC(mem);
		Native.ReleaseDC(0, screen);
		using var f = File.Create(path);
		using var bw = new BinaryWriter(f);
		bw.Write((ushort)0x4D42); bw.Write(54 + pixels.Length); bw.Write(0); bw.Write(54);
		bw.Write(40); bw.Write(w); bw.Write(h); bw.Write((ushort)1); bw.Write((ushort)24); bw.Write(0); bw.Write(pixels.Length); bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0);
		bw.Write(pixels);
	}
}

public static class Native
{
	public delegate nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam);
	[StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
	[StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
	[StructLayout(LayoutKind.Sequential)] public struct MSG { public nint hwnd; public uint message; public nint wParam, lParam; public uint time; public POINT pt; public uint lPrivate; }
	[StructLayout(LayoutKind.Sequential)] public struct BITMAPINFOHEADER { public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount; public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant; }
	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	public struct WNDCLASSEXW
	{
		public uint cbSize, style;
		public nint lpfnWndProc;
		public int cbClsExtra, cbWndExtra;
		public nint hInstance, hIcon, hCursor, hbrBackground;
		public string? lpszMenuName;
		public string lpszClassName;
		public nint hIconSm;
	}

	public static RECT WindowRect(nint hwnd)
	{
		GetWindowRect(hwnd, out var r);
		return r;
	}

	[DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] public static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);
	[DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] public static extern nint CreateWindowExW(uint exStyle, string cls, string name, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);
	[DllImport("user32.dll")] public static extern nint DefWindowProcW(nint hwnd, uint msg, nint wParam, nint lParam);
	[DllImport("user32.dll")] public static extern int GetMessageW(out MSG msg, nint hwnd, uint min, uint max);
	[DllImport("user32.dll")] public static extern bool TranslateMessage(ref MSG msg);
	[DllImport("user32.dll")] public static extern nint DispatchMessageW(ref MSG msg);
	[DllImport("user32.dll")] public static extern void PostQuitMessage(int code);
	[DllImport("user32.dll")] public static extern nint LoadCursorW(nint instance, nint name);
	[DllImport("user32.dll")] public static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int w, int h, uint flags);
	[DllImport("user32.dll")] public static extern bool ShowWindow(nint hwnd, int cmd);
	[DllImport("user32.dll")] public static extern bool IsWindowVisible(nint hwnd);
	[DllImport("user32.dll")] public static extern bool DestroyWindow(nint hwnd);
	[DllImport("user32.dll")] public static extern bool ClientToScreen(nint hwnd, ref POINT p);
	[DllImport("user32.dll")] public static extern bool GetClientRect(nint hwnd, out RECT r);
	[DllImport("user32.dll")] public static extern bool GetWindowRect(nint hwnd, out RECT r);
	[DllImport("user32.dll")] public static extern uint GetDpiForWindow(nint hwnd);
	[DllImport("user32.dll")] public static extern bool SetForegroundWindow(nint hwnd);
	[DllImport("user32.dll")] public static extern nint WindowFromPoint(POINT p);
	[DllImport("user32.dll")] public static extern nint GetAncestor(nint hwnd, uint flags);
	[DllImport("user32.dll")] public static extern nint GetParent(nint hwnd);
	[DllImport("user32.dll")] public static extern bool IsChild(nint parent, nint hwnd);
	[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassNameW(nint hwnd, StringBuilder name, int max);
	[DllImport("user32.dll", SetLastError = true)] public static extern bool SetLayeredWindowAttributes(nint hwnd, uint key, byte alpha, uint flags);
	[DllImport("user32.dll")] public static extern int SetWindowRgn(nint hwnd, nint rgn, bool redraw);
	[DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(nint value);
	[DllImport("user32.dll")] public static extern nint GetDC(nint hwnd);
	[DllImport("user32.dll")] public static extern int ReleaseDC(nint hwnd, nint dc);
	[DllImport("gdi32.dll")] public static extern uint GetPixel(nint dc, int x, int y);
	[DllImport("gdi32.dll")] public static extern nint CreateRectRgn(int l, int t, int r, int b);
	[DllImport("gdi32.dll")] public static extern int CombineRgn(nint dest, nint a, nint b, int mode);
	[DllImport("gdi32.dll")] public static extern bool DeleteObject(nint obj);
	[DllImport("gdi32.dll")] public static extern nint GetStockObject(int i);
	[DllImport("gdi32.dll")] public static extern nint CreateCompatibleDC(nint dc);
	[DllImport("gdi32.dll")] public static extern nint CreateCompatibleBitmap(nint dc, int w, int h);
	[DllImport("gdi32.dll")] public static extern nint SelectObject(nint dc, nint obj);
	[DllImport("gdi32.dll")] public static extern bool DeleteDC(nint dc);
	[DllImport("gdi32.dll")] public static extern bool BitBlt(nint dest, int x, int y, int w, int h, nint src, int sx, int sy, uint rop);
	[DllImport("gdi32.dll")] public static extern int GetDIBits(nint dc, nint bmp, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER info, uint usage);
	[DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern nint GetModuleHandleW(string? name);
}
