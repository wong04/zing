using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Windows.UI.Composition;

// W-6 spike: visual hosting. ONE top-level HWND owns a Windows.UI.Composition visual tree (Compositor +
// DesktopWindowTarget). Chrome, page and overlay are CoreWebView2CompositionControllers whose
// RootVisualTarget is a ContainerVisual in that tree, so z-order is the order of the visuals, not of windows.
// The app owns what the windowed controllers did for free: it forwards mouse input to the view under the
// pointer (SendMouseInput), sets the cursor (CursorChanged), moves keyboard focus (MoveFocus), forwards OLE
// drag-and-drop (ICoreWebView2CompositionControllerInterop2) and answers UI Automation (WM_GETOBJECT).
//
// Environment:
//   ZING_SPIKE_CDP        remote debugging port for Playwright (off by default)
//   ZING_E2E=1            test run: window off-screen, out of the taskbar, never activated; test messages on
//   ZING_SPIKE_ONSCREEN=1 with ZING_E2E=1, still place the window on screen (to watch a check run)
// Log: %TEMP%\zing-visual-spike.log
public static class Program
{
	[STAThread]
	public static void Main()
	{
		// Per-monitor v2 before any window exists: visuals and WebView2 bounds are in physical pixels.
		Native.SetProcessDpiAwarenessContext(-4);
		// OLE, not just COM, for RegisterDragDrop. [STAThread] already made this thread an STA.
		Native.OleInitialize(0);
		// Windows.UI.Composition needs a system DispatcherQueue on the UI thread (Zing.Host's is the App SDK's
		// Microsoft.UI one; a switch would need this one too, or Microsoft.UI.Composition if WebView2 takes it).
		var queue = Composition.CreateDispatcherQueueOnCurrentThread();
		SynchronizationContext.SetSynchronizationContext(new QueueContext(queue.DispatcherQueue));
		var window = new SpikeWindow();
		_ = window.StartAsync().ContinueWith(t => SpikeWindow.Write($"start failed: {t.Exception}"), TaskContinuationOptions.OnlyOnFaulted);
		while (Native.GetMessageW(out var msg, 0, 0, 0) > 0)
		{
			Native.TranslateMessage(ref msg);
			Native.DispatchMessageW(ref msg);
		}
		GC.KeepAlive(queue);
		GC.KeepAlive(window);
	}
}

/// <summary>Resumes awaits on the UI thread through the system DispatcherQueue.</summary>
internal sealed class QueueContext(Windows.System.DispatcherQueue queue) : SynchronizationContext
{
	public override void Post(SendOrPostCallback d, object? state)
	{
		if (!queue.TryEnqueue(() => d(state))) SpikeWindow.Write("TryEnqueue failed");
	}

	public override void Send(SendOrPostCallback d, object? state)
	{
		if (!queue.HasThreadAccess) throw new NotSupportedException("Send from another thread");
		d(state);
	}

	public override SynchronizationContext CreateCopy() => this;
}

public sealed class SpikeWindow
{
	public static readonly string Log = Path.Combine(Path.GetTempPath(), "zing-visual-spike.log");
	private const double ChromeHeight = 56;
	private static readonly bool TestRun = Environment.GetEnvironmentVariable("ZING_E2E") == "1";

	private readonly Native.WndProc _proc;
	private readonly nint _hwnd;
	private readonly Compositor _compositor;
	private readonly Windows.UI.Composition.Desktop.DesktopWindowTarget _target;
	private readonly ContainerVisual _root;
	private readonly SpriteVisual _background;
	private readonly List<string> _events = [];
	private DropTarget? _drop;
	private CompositedView? _chrome;
	private CompositedView? _page;
	private CompositedView? _overlay;
	private CompositedView? _hovered;
	private CompositedView? _capture;
	private CompositedView? _focused;
	private CompositedView? _dragView;
	private nint _dragData;
	private bool _tracking;
	private double _scale = 1;

	public SpikeWindow()
	{
		File.WriteAllText(Log, "");
		_proc = WndProc;
		const string cls = "ZingVisualHostingSpike";
		const uint CS_DBLCLKS = 0x8;
		var wc = new Native.WNDCLASSEXW { cbSize = (uint)Marshal.SizeOf<Native.WNDCLASSEXW>(), style = CS_DBLCLKS, lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc), hInstance = Native.GetModuleHandleW(null), hCursor = Native.LoadCursorW(0, 32512), lpszClassName = cls };
		if (Native.RegisterClassExW(ref wc) == 0) throw new InvalidOperationException($"RegisterClassEx failed: {Marshal.GetLastWin32Error()}");
		const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
		// No redirection bitmap: everything in the client area is the composition tree, as in the
		// Windows.UI.Composition Win32 samples. Without it GDI's surface would sit under the visuals.
		const uint WS_EX_NOREDIRECTIONBITMAP = 0x00200000, WS_EX_TOOLWINDOW = 0x80;
		var offScreen = TestRun && Environment.GetEnvironmentVariable("ZING_SPIKE_ONSCREEN") != "1";
		var ex = WS_EX_NOREDIRECTIONBITMAP | (TestRun ? WS_EX_TOOLWINDOW : 0);
		_hwnd = Native.CreateWindowExW(ex, cls, "Visual hosting spike", WS_OVERLAPPEDWINDOW, offScreen ? -20000 : 100, offScreen ? -20000 : 100, 1200, 800, 0, 0, wc.hInstance, 0);
		if (_hwnd == 0) throw new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastWin32Error()}");
		_scale = Native.GetDpiForWindow(_hwnd) / 96.0;
		Native.SetWindowPos(_hwnd, 0, 0, 0, (int)(1200 * _scale), (int)(800 * _scale), 0x2 | 0x4 | 0x10); // NOMOVE | NOZORDER | NOACTIVATE

		_compositor = new Compositor();
		_target = Composition.CreateDesktopWindowTarget(_compositor, _hwnd);
		_root = _compositor.CreateContainerVisual();
		_root.RelativeSizeAdjustment = System.Numerics.Vector2.One;
		_background = _compositor.CreateSpriteVisual();
		_background.Brush = _compositor.CreateColorBrush(Windows.UI.Color.FromArgb(255, 32, 32, 32));
		_background.RelativeSizeAdjustment = System.Numerics.Vector2.One;
		_root.Children.InsertAtBottom(_background);
		_target.Root = _root;

		Native.ShowWindow(_hwnd, TestRun ? 4 : 5); // SW_SHOWNOACTIVATE in test runs
	}

	public async Task StartAsync()
	{
		var options = new CoreWebView2EnvironmentOptions();
		var cdp = Environment.GetEnvironmentVariable("ZING_SPIKE_CDP");
		if (!string.IsNullOrEmpty(cdp)) options.AdditionalBrowserArguments = $"--remote-debugging-port={cdp}";
		var env = await CoreWebView2Environment.CreateWithOptionsAsync(null, Path.Combine(Path.GetTempPath(), $"zing-visual-spike-{Environment.ProcessId}"), options);
		var web = Path.Combine(AppContext.BaseDirectory, "web");
		// Creation order is z-order: each view's visual goes on top of the ones before it.
		_chrome = await CompositedView.CreateAsync(env, _hwnd, _compositor, _root, "chrome", web, transparent: false);
		_page = await CompositedView.CreateAsync(env, _hwnd, _compositor, _root, "page", web, transparent: false);
		_overlay = await CompositedView.CreateAsync(env, _hwnd, _compositor, _root, "overlay", web, transparent: true);
		// The overlay takes input only where it reports content; until it reports, nowhere.
		_overlay.HitRects = [];
		foreach (var view in Views())
		{
			view.Controller.GotFocus += (_, _) => { _focused = view; Event($"focus {view.Name}"); };
			view.Controller.LostFocus += (_, _) => Event($"blur {view.Name}");
			view.Controller.MoveFocusRequested += (_, e) => OnMoveFocusRequested(view, e);
			view.Controller.AcceleratorKeyPressed += (_, e) => OnKey(view, e);
			view.Composition.CursorChanged += (_, _) => OnCursorChanged(view);
			view.Core.WebMessageReceived += (_, e) => OnMessage(view, e.WebMessageAsJson);
		}
		_drop = new DropTarget(this);
		var hr = Native.RegisterDragDrop(_hwnd, _drop);
		if (hr != 0) Write($"RegisterDragDrop failed 0x{hr:X8}");
		Layout();
		_chrome.Navigate("chrome.html");
		_page.Navigate("page.html");
		_overlay.Navigate("overlay.html");
		Write("ready");
	}

	private IEnumerable<CompositedView> Views() => new[] { _chrome, _page, _overlay }.OfType<CompositedView>();

	/// <summary>Topmost view that wants the client point: overlay only over its reported content, then chrome, then page.</summary>
	private CompositedView? HitTest(int x, int y)
	{
		foreach (var view in new[] { _overlay, _chrome, _page })
			if (view is not null && view.Wants(x, y)) return view;
		return null;
	}

	private nint WndProc(nint hwnd, uint msg, nint w, nint l)
	{
		try
		{
			switch (msg)
			{
				case >= 0x0200 and <= 0x020E: // WM_MOUSEMOVE .. WM_MOUSEHWHEEL
					OnMouse(msg, w, l);
					return 0;
				case 0x02A3: // WM_MOUSELEAVE
					_tracking = false;
					if (_capture is null) Hover(null);
					return 0;
				case 0x0215: // WM_CAPTURECHANGED
					_capture = null;
					return 0;
				case 0x0020: // WM_SETCURSOR
					if ((l & 0xFFFF) == 1 && _hovered is { } over) // HTCLIENT
					{
						Native.SetCursor(over.CursorHandle);
						return 1;
					}
					break;
				case 0x003D: // WM_GETOBJECT
					if ((int)(long)l == -25 && AutomationRoot() is { } provider) // UiaRootObjectId
					{
						var result = Native.UiaReturnRawElementProvider(hwnd, w, l, provider);
						Marshal.Release(provider);
						return result;
					}
					break;
				case 0x0005: // WM_SIZE
					Layout();
					break;
				case 0x0003: // WM_MOVE
					foreach (var view in Views()) view.Controller.NotifyParentWindowPositionChanged();
					break;
				case 0x0006: // WM_ACTIVATE
					// Activation by Alt+Tab / taskbar gives the HWND focus; hand it back to the view that had it.
					if ((w & 0xFFFF) == 1 && _focused is { } last)
					{
						last.Focus();
						return 0;
					}
					break;
				case 0x0007: // WM_SETFOCUS: the HWND itself got focus (e.g. after a menu); forward it to a view.
					(_focused ?? _page)?.Focus();
					return 0;
				case 0x02E0: // WM_DPICHANGED
					_scale = (w & 0xFFFF) / 96.0;
					var r = Marshal.PtrToStructure<Native.RECT>(l);
					Native.SetWindowPos(hwnd, 0, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, 0x14);
					Layout();
					return 0;
				case 0x0002: // WM_DESTROY
					if (_drop is not null) Native.RevokeDragDrop(hwnd);
					Native.PostQuitMessage(0);
					return 0;
			}
		}
		catch (Exception e)
		{
			// Never let an exception unwind through the native window procedure.
			Write($"WndProc 0x{msg:X4} failed: {e}");
		}
		return Native.DefWindowProcW(hwnd, msg, w, l);
	}

	// ---- Mouse --------------------------------------------------------------------------------------

	private void OnMouse(uint msg, nint w, nint l)
	{
		const uint WM_MOUSEMOVE = 0x200, WM_MOUSEWHEEL = 0x20A, WM_MOUSEHWHEEL = 0x20E, WM_XBUTTONDOWN = 0x20B, WM_XBUTTONUP = 0x20C, WM_XBUTTONDBLCLK = 0x20D;
		var point = new Native.POINT { X = (short)(l & 0xFFFF), Y = (short)((l >> 16) & 0xFFFF) };
		// Wheel messages carry screen coordinates; the rest are client coordinates.
		if (msg is WM_MOUSEWHEEL or WM_MOUSEHWHEEL) Native.ScreenToClient(_hwnd, ref point);
		var target = _capture ?? HitTest(point.X, point.Y);
		if (msg == WM_MOUSEMOVE)
		{
			Hover(target);
			// Real input asks for WM_MOUSELEAVE. A test run posts synthetic moves while the real cursor is
			// elsewhere, so tracking would report a leave at once.
			if (!_tracking && !TestRun)
			{
				var track = new Native.TRACKMOUSEEVENT { cbSize = (uint)Marshal.SizeOf<Native.TRACKMOUSEEVENT>(), dwFlags = 0x2, hwndTrack = _hwnd }; // TME_LEAVE
				_tracking = Native.TrackMouseEvent(ref track);
			}
		}
		if (target is null) return;
		var isDown = msg is 0x201 or 0x203 or 0x204 or 0x206 or 0x207 or 0x209 or WM_XBUTTONDOWN or WM_XBUTTONDBLCLK;
		var isUp = msg is 0x202 or 0x205 or 0x208 or WM_XBUTTONUP;
		if (isDown)
		{
			if (_capture is null)
			{
				Native.SetCapture(_hwnd);
				_capture = target;
			}
			// Keyboard focus follows the click. The view would not take it by itself: with visual hosting
			// the HWND has the input, and only MoveFocus tells WebView2 which view should get keys.
			if (_focused != target) target.Focus();
		}
		var keys = (CoreWebView2MouseEventVirtualKeys)(int)(w & 0xFFFF);
		uint data = msg switch
		{
			WM_MOUSEWHEEL or WM_MOUSEHWHEEL => (uint)(int)(short)((w >> 16) & 0xFFFF),
			WM_XBUTTONDOWN or WM_XBUTTONUP or WM_XBUTTONDBLCLK => (uint)((w >> 16) & 0xFFFF),
			_ => 0,
		};
		// CoreWebView2MouseEventKind's values are the WM_ message numbers.
		target.Composition.SendMouseInput((CoreWebView2MouseEventKind)msg, keys, data, target.ToView(point));
		if (msg != WM_MOUSEMOVE) Event($"mouse 0x{msg:X3} -> {target.Name}");
		const int Buttons = 0x1 | 0x2 | 0x10 | 0x20 | 0x40; // MK_LBUTTON | MK_RBUTTON | MK_MBUTTON | MK_XBUTTON1 | MK_XBUTTON2
		// wParam of an up message still lists the other buttons held; release capture when none are.
		if (isUp && ((int)keys & Buttons) == 0)
		{
			Native.ReleaseCapture();
			_capture = null;
		}
	}

	private void Hover(CompositedView? view)
	{
		if (view == _hovered) return;
		// A view the pointer left gets a Leave, or it keeps its :hover state and tooltip.
		_hovered?.Composition.SendMouseInput(CoreWebView2MouseEventKind.Leave, CoreWebView2MouseEventVirtualKeys.None, 0, default);
		_hovered = view;
		if (view is not null) Native.SetCursor(view.CursorHandle);
	}

	private void OnCursorChanged(CompositedView view)
	{
		view.UpdateCursor();
		Event($"cursor {view.Name} {view.CursorName}");
		if (view == _hovered) Native.SetCursor(view.CursorHandle);
	}

	// ---- Keyboard and focus -------------------------------------------------------------------------

	private void OnMoveFocusRequested(CompositedView view, CoreWebView2MoveFocusRequestedEventArgs e)
	{
		// Tab past the last element of a view: move to the next view in chrome -> page -> overlay order.
		var order = Views().Where(v => v != _overlay || _overlay.HitRects is { Count: > 0 }).ToList();
		var i = order.IndexOf(view);
		if (i < 0 || order.Count < 2) return;
		var next = e.Reason == CoreWebView2MoveFocusReason.Previous ? order[(i - 1 + order.Count) % order.Count] : order[(i + 1) % order.Count];
		next.Controller.MoveFocus(e.Reason);
		e.Handled = true;
	}

	private void OnKey(CompositedView view, CoreWebView2AcceleratorKeyPressedEventArgs e)
	{
		if (e.KeyEventKind is not (CoreWebView2KeyEventKind.KeyDown or CoreWebView2KeyEventKind.SystemKeyDown)) return;
		Event($"key {view.Name} vk=0x{e.VirtualKey:X}");
		// Escape anywhere toggles the overlay's panel, as Zing's dropdowns close on Escape.
		if (e.VirtualKey == 0x1B) _overlay?.Core.PostWebMessageAsJson("""{"toggle":true}""");
	}

	// ---- Drag and drop (OLE), forwarded to the view under the pointer --------------------------------

	internal uint DragEnter(nint data, uint keyState, Native.POINT screen, uint effect)
	{
		Native.AddRef(data);
		_dragData = data;
		return DragOver(keyState, screen, effect);
	}

	internal uint DragOver(uint keyState, Native.POINT screen, uint effect)
	{
		var point = screen;
		Native.ScreenToClient(_hwnd, ref point);
		var view = HitTest(point.X, point.Y);
		if (view != _dragView)
		{
			_dragView?.Interop.DragLeave();
			_dragView = view;
			if (view is not null)
			{
				Event($"drag enter {view.Name}");
				return view.Interop.DragEnter(_dragData, keyState, view.ToViewPoint(point), effect);
			}
		}
		return view?.Interop.DragOver(keyState, view.ToViewPoint(point), effect) ?? 0;
	}

	internal void DragLeave()
	{
		_dragView?.Interop.DragLeave();
		_dragView = null;
		ReleaseDragData();
	}

	internal uint Drop(nint data, uint keyState, Native.POINT screen, uint effect)
	{
		var point = screen;
		Native.ScreenToClient(_hwnd, ref point);
		var view = HitTest(point.X, point.Y);
		if (view != _dragView) _dragView?.Interop.DragLeave();
		var result = view?.Interop.Drop(data, keyState, view.ToViewPoint(point), effect) ?? 0;
		Event($"drop {view?.Name ?? "nowhere"}");
		_dragView = null;
		ReleaseDragData();
		return result;
	}

	private void ReleaseDragData()
	{
		if (_dragData != 0) Marshal.Release(_dragData);
		_dragData = 0;
	}

	// ---- UI Automation ------------------------------------------------------------------------------

	/// <summary>
	/// Minimal UIA: the window's root provider is the provider of ONE view, the focused one (or the overlay
	/// while it shows content). This is Microsoft's single-view visual hosting pattern; a real tree of three
	/// views needs a fragment root of our own (see README, "UI Automation").
	/// </summary>
	private nint AutomationRoot()
	{
		var view = _focused ?? (_overlay?.HitRects is { Count: > 0 } ? _overlay : _page);
		return view?.Interop.AutomationProviderSimple() ?? 0;
	}

	// ---- Layout -------------------------------------------------------------------------------------

	private void Layout()
	{
		if (_chrome is null || _page is null || _overlay is null) return;
		Native.GetClientRect(_hwnd, out var client);
		var chrome = (int)Math.Round(ChromeHeight * _scale);
		var pageHeight = Math.Max(0, client.Bottom - chrome);
		_chrome.Place(0, 0, client.Right, chrome, _scale);
		_page.Place(0, chrome, client.Right, pageHeight, _scale);
		// The overlay covers the whole page; it is transparent and takes input only over its content.
		_overlay.Place(0, chrome, client.Right, pageHeight, _scale);
	}

	// ---- Messages from the views (and test messages, in test runs only) ------------------------------

	private void OnMessage(CompositedView view, string json)
	{
		using var doc = JsonDocument.Parse(json);
		var root = doc.RootElement;
		if (view == _overlay && root.TryGetProperty("hit", out var hit))
		{
			// CSS pixels inside the overlay (zoom 1) to physical pixels relative to the view.
			_overlay.HitRects = hit.EnumerateArray().Select(r => new Native.RECT
			{
				Left = (int)Math.Floor(r.GetProperty("x").GetDouble() * _scale),
				Top = (int)Math.Floor(r.GetProperty("y").GetDouble() * _scale),
				Right = (int)Math.Ceiling((r.GetProperty("x").GetDouble() + r.GetProperty("width").GetDouble()) * _scale),
				Bottom = (int)Math.Ceiling((r.GetProperty("y").GetDouble() + r.GetProperty("height").GetDouble()) * _scale),
			}).ToList();
			Event($"overlay hit rects {_overlay.HitRects.Count}");
			return;
		}
		if (view == _overlay && root.TryGetProperty("chose", out var chose))
		{
			Event($"overlay chose {chose.GetInt32()}");
			_page?.Core.PostWebMessageAsJson($$"""{"chosen":{{chose.GetInt32()}}}""");
			return;
		}
		if (TestRun && view == _chrome && root.TryGetProperty("test", out var test)) OnTestMessage(test.GetString() ?? "", root);
	}

	/// <summary>Test seam: synthetic mouse messages go through the same WndProc path as real ones (only the OS hit-test of the window is skipped).</summary>
	private void OnTestMessage(string test, JsonElement message)
	{
		var id = message.TryGetProperty("id", out var i) ? i.GetInt32() : 0;
		int X() => (int)Math.Round(message.GetProperty("x").GetDouble() * _scale);
		int Y() => (int)Math.Round(message.GetProperty("y").GetDouble() * _scale);
		static nint Pack(int x, int y) => (nint)(((y & 0xFFFF) << 16) | (x & 0xFFFF));
		object? reply = null;
		switch (test)
		{
			case "move":
				Native.PostMessageW(_hwnd, 0x200, 0, Pack(X(), Y()));
				break;
			case "click":
				Native.PostMessageW(_hwnd, 0x200, 0, Pack(X(), Y()));
				Native.PostMessageW(_hwnd, 0x201, 0x1, Pack(X(), Y()));
				Native.PostMessageW(_hwnd, 0x202, 0, Pack(X(), Y()));
				break;
			case "pixel":
				var (r, g, b) = Native.ClientPixel(_hwnd, X(), Y());
				reply = new { r, g, b };
				break;
			case "state":
				reply = new
				{
					scale = _scale,
					focused = _focused?.Name,
					hovered = _hovered?.Name,
					cursors = Views().ToDictionary(v => v.Name, v => v.CursorName),
					views = Views().ToDictionary(v => v.Name, v => new { x = v.Bounds.Left / _scale, y = v.Bounds.Top / _scale, width = (v.Bounds.Right - v.Bounds.Left) / _scale, height = (v.Bounds.Bottom - v.Bounds.Top) / _scale }),
					overlayHitRects = _overlay?.HitRects?.Count ?? -1,
					events = _events.ToArray(),
				};
				break;
		}
		// The synthetic input above is queued, not yet handled; the checks poll "state" for its effect.
		_chrome?.Core.PostWebMessageAsJson(JsonSerializer.Serialize(new { id, reply }));
	}

	private void Event(string line)
	{
		_events.Add(line);
		if (_events.Count > 200) _events.RemoveAt(0);
		Write(line);
	}

	public static void Write(string line) => File.AppendAllText(Log, $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}");
}
