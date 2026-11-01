using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;

// Spike 3: the main window is a plain Win32 window that owns every WebView2 directly, as Microsoft's
// WebView2Browser sample does (BrowserWindow.cpp: CreateWindowW + CreateCoreWebView2Controller(m_hWnd)).
// Spike 2 hosted the same views inside a WinUI window, and the trace showed WinUI's content window
// (InputSiteWindowClass) taking keyboard focus on every activation, so keys never reached the views.
// Checks: clicks and Ctrl+F reach every view (AcceleratorKeyPressed), a Win32 menu above the views,
// dragging by the strip, and the see-through overlay.
public static class Program
{
	[STAThread]
	public static void Main()
	{
		WinRT.ComWrappersSupport.InitializeComWrappers();
		var dispatcher = Microsoft.UI.Dispatching.DispatcherQueueController.CreateOnCurrentThread();
		SynchronizationContext.SetSynchronizationContext(new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(dispatcher.DispatcherQueue));
		var window = new SpikeWindow();
		_ = window.StartAsync().ContinueWith(t => File.AppendAllText(SpikeWindow.Log, t.Exception!.ToString()), TaskContinuationOptions.OnlyOnFaulted);
		while (Native.GetMessageW(out var msg, 0, 0, 0) > 0)
		{
			Native.TranslateMessage(ref msg);
			Native.DispatchMessageW(ref msg);
		}
		GC.KeepAlive(dispatcher);
	}
}

public sealed class SpikeWindow
{
	private const int StripHeight = 80;
	public static readonly string Log = Path.Combine(Path.GetTempPath(), "zing-spike2.log");

	private const string Strip = """
		<!doctype html><title>Strip</title>
		<body style="margin:0;height:100vh;display:flex;align-items:center;gap:12px;padding:0 16px;background:#2b2520;color:#eee;font:16px system-ui">
		<div style="app-region:drag;-webkit-app-region:drag;flex:1;align-self:stretch;display:flex;align-items:center">Drag the window by this strip</div>
		<button id=menu>Menu</button>
		<input placeholder="Type here, press Ctrl+F" style="width:260px">
		<script>menu.onclick = (e) => chrome.webview.postMessage({ menu: [e.clientX, e.clientY] })</script>
		""";

	private const string Clock = """
		<!doctype html><title>Live page</title>
		<body style="margin:0;height:100vh;display:grid;place-items:center;font:bold 10vw system-ui;
			background:linear-gradient(135deg,#ff5f6d,#ffc371,#47e5bc,#4facfe)">
		<div id=t></div><p style="font:20px system-ui">needle needle needle</p>
		<script>setInterval(() => t.textContent = new Date().toLocaleTimeString(), 100)</script>
		""";

	private const string Overlay = """
		<!doctype html><title>Overlay</title>
		<body style="margin:0;height:100vh;display:grid;place-items:center;background:transparent;font:20px system-ui">
		<div style="background:#fff;padding:32px;border-radius:16px;box-shadow:0 4px 16px rgb(0 0 0 / .15)">
			<p>Overlay. Press Escape to hide it.</p>
			<button onclick="this.textContent = 'Clicked ' + (++window.n || (window.n = 1))">Click me</button>
		</div>
		""";

	private readonly Native.WndProc _proc;
	private readonly nint _hwnd;
	private HostedView? _strip;
	private HostedView? _page;
	private HostedView? _overlay;
	private HostedView? _focused;

	public Microsoft.UI.Windowing.AppWindow AppWindow { get; }

	public SpikeWindow()
	{
		File.WriteAllText(Log, "");
		_proc = WndProc;
		const string cls = "ZingSpikeWindow";
		var wc = new Native.WNDCLASSEXW { cbSize = (uint)Marshal.SizeOf<Native.WNDCLASSEXW>(), lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc), hInstance = Native.GetModuleHandleW(null), hCursor = Native.LoadCursorW(0, 32512), lpszClassName = cls };
		if (Native.RegisterClassExW(ref wc) == 0) throw new InvalidOperationException($"RegisterClassEx failed: {Marshal.GetLastWin32Error()}");
		const uint WS_OVERLAPPEDWINDOW = 0x00CF0000, WS_CLIPCHILDREN = 0x02000000;
		_hwnd = Native.CreateWindowExW(0, cls, "Overlay spike 3", WS_OVERLAPPEDWINDOW | WS_CLIPCHILDREN, 100, 100, 1300, 850, 0, 0, wc.hInstance, 0);
		if (_hwnd == 0) throw new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastWin32Error()}");
		// WinUI's windowing API works on any top-level window: Windows' own caption buttons (with Snap
		// Layouts) over the strip, with no XAML content in the window.
		AppWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hwnd));
		AppWindow.TitleBar.ExtendsContentIntoTitleBar = true;
		Native.ShowWindow(_hwnd, 5);
	}

	public async Task StartAsync()
	{
		var env = await CoreWebView2Environment.CreateWithOptionsAsync(null, Path.Combine(Path.GetTempPath(), "zing-overlay-spike3"), new CoreWebView2EnvironmentOptions());
		_strip = await HostedView.CreateAsync(_hwnd, env, "strip", Strip, nonClientRegions: true, transparent: false, OnKey);
		_page = await HostedView.CreateAsync(_hwnd, env, "page", Clock, nonClientRegions: false, transparent: false, OnKey);
		_overlay = await HostedView.CreateAsync(HostedView.CreatePopup(_hwnd), env, "overlay", Overlay, nonClientRegions: false, transparent: true, OnKey);
		_strip.Core.WebMessageReceived += (_, e) => ShowMenu(e.WebMessageAsJson);
		foreach (var view in new[] { _strip, _page, _overlay })
		{
			view.Controller.GotFocus += (_, _) => { _focused = view; Write($"focus in {view.Name}"); };
			view.Controller.LostFocus += (_, _) => Write($"focus out {view.Name}");
		}
		Layout();
		Write("ready");
	}

	private nint WndProc(nint hwnd, uint msg, nint w, nint l)
	{
		switch (msg)
		{
			case 0x0005: // WM_SIZE
				Layout();
				break;
			case 0x0003: // WM_MOVE
				Layout();
				foreach (var view in Views()) view.Controller.NotifyParentWindowPositionChanged();
				break;
			case 0x0006: // WM_ACTIVATE
				Write($"WM_ACTIVATE {w & 0xFFFF}");
				// DefWindowProc gives the window itself focus on activation; hand it back to the view that had it,
				// as a Win32 app restores its focused control. Only for WA_ACTIVE (Alt+Tab, taskbar): on
				// WA_CLICKACTIVE the click itself puts focus where it landed.
				if ((w & 0xFFFF) == 1 && _focused is { } last)
				{
					last.Controller.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);
					return 0;
				}
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

	private IEnumerable<HostedView> Views() => new[] { _strip, _page, _overlay }.OfType<HostedView>();

	private void ShowMenu(string json)
	{
		Write($"message {json}");
		using var doc = System.Text.Json.JsonDocument.Parse(json);
		var at = doc.RootElement.GetProperty("menu");
		var scale = Native.GetDpiForWindow(_hwnd) / 96.0;
		var point = new Native.POINT { X = (int)(at[0].GetDouble() * scale), Y = (int)((at[1].GetDouble() + 20) * scale) };
		Native.ClientToScreen(_hwnd, ref point);
		var menu = Native.CreatePopupMenu();
		Native.AppendMenuW(menu, 0, 1, "A Windows menu above the hosted views");
		Native.AppendMenuW(menu, 0, 2, "Second item");
		const uint TPM_RETURNCMD = 0x100, TPM_RIGHTBUTTON = 0x2;
		var chosen = Native.TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, point.X, point.Y, _hwnd, 0);
		Native.DestroyMenu(menu);
		Write($"menu chose {chosen}");
	}

	private void OnKey(string view, CoreWebView2AcceleratorKeyPressedEventArgs e)
	{
		if (e.KeyEventKind is not (CoreWebView2KeyEventKind.KeyDown or CoreWebView2KeyEventKind.SystemKeyDown)) return;
		var ctrl = (Native.GetKeyState(0x11) & 0x8000) != 0;
		Write($"key {view} vk=0x{e.VirtualKey:X} ctrl={ctrl}");
		// Ctrl+F is Zing's, not Edge's built-in find.
		if (ctrl && e.VirtualKey == 0x46)
		{
			e.Handled = true;
			Write($"handled ctrl+f from {view}");
		}
		if (e.VirtualKey == 0x1B && _overlay is not null) _overlay.Visible = false;
	}

	private void Layout()
	{
		if (_strip is null || _page is null || _overlay is null) return;
		Native.GetClientRect(_hwnd, out var client);
		var scale = Native.GetDpiForWindow(_hwnd) / 96.0;
		var strip = (int)(StripHeight * scale);
		// Views owned directly by the main window take their place through Bounds alone, as in the sample.
		_strip.Controller.Bounds = new Windows.Foundation.Rect(0, 0, client.Right, strip);
		_page.Controller.Bounds = new Windows.Foundation.Rect(0, strip, client.Right, Math.Max(0, client.Bottom - strip));
		// No caption rectangles of our own: the strip's CSS app-region:drag is the only drag area.
		Microsoft.UI.Input.InputNonClientPointerSource.GetForWindowId(AppWindow.Id).SetRegionRects(Microsoft.UI.Input.NonClientRegionKind.Caption, []);
		var origin = new Native.POINT();
		Native.ClientToScreen(_hwnd, ref origin);
		_overlay.PlacePopup(origin.X + client.Right / 4, origin.Y + strip + 40, client.Right / 2, (client.Bottom - strip) / 2);
	}

	private static void Write(string line) => File.AppendAllText(Log, $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}");
}

/// <summary>A WebView2 the app hosts itself: owned directly by the main window, or by an owned popup.</summary>
public sealed class HostedView
{
	private static readonly Native.WndProc PopupProc = (h, m, w, l) => Native.DefWindowProcW(h, m, w, l);
	private static bool _registered;
	private const string PopupClass = "ZingOverlayPopup";

	private readonly nint _window;

	private HostedView(nint window, CoreWebView2Controller controller, string name)
	{
		_window = window;
		Controller = controller;
		Name = name;
	}

	public string Name { get; }
	public CoreWebView2Controller Controller { get; }
	public CoreWebView2 Core => Controller.CoreWebView2;

	public bool Visible
	{
		set => Native.ShowWindow(_window, value ? 5 : 0);
	}

	public static nint CreatePopup(nint owner)
	{
		if (!_registered)
		{
			var wc = new Native.WNDCLASSEXW { cbSize = (uint)Marshal.SizeOf<Native.WNDCLASSEXW>(), lpfnWndProc = Marshal.GetFunctionPointerForDelegate(PopupProc), hInstance = Native.GetModuleHandleW(null), lpszClassName = PopupClass };
			if (Native.RegisterClassExW(ref wc) == 0) throw new InvalidOperationException($"RegisterClassEx failed: {Marshal.GetLastWin32Error()}");
			_registered = true;
		}
		const uint WS_POPUP = 0x80000000, WS_VISIBLE = 0x10000000;
		const uint WS_EX_TOOLWINDOW = 0x80, WS_EX_NOREDIRECTIONBITMAP = 0x200000;
		var hwnd = Native.CreateWindowExW(WS_EX_TOOLWINDOW | WS_EX_NOREDIRECTIONBITMAP, PopupClass, "overlay", WS_POPUP | WS_VISIBLE, 0, 0, 100, 100, owner, 0, Native.GetModuleHandleW(null), 0);
		if (hwnd == 0) throw new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastWin32Error()}");
		return hwnd;
	}

	public static async Task<HostedView> CreateAsync(nint window, CoreWebView2Environment env, string name, string html, bool nonClientRegions, bool transparent, Action<string, CoreWebView2AcceleratorKeyPressedEventArgs> onKey)
	{
		var controller = await env.CreateCoreWebView2ControllerAsync(CoreWebView2ControllerWindowReference.CreateFromWindowHandle((ulong)window));
		if (transparent) controller.DefaultBackgroundColor = Windows.UI.Color.FromArgb(0, 255, 255, 255);
		controller.CoreWebView2.Settings.IsNonClientRegionSupportEnabled = nonClientRegions;
		controller.AcceleratorKeyPressed += (_, e) => onKey(name, e);
		controller.CoreWebView2.NavigateToString(html);
		return new HostedView(window, controller, name);
	}

	public void PlacePopup(int x, int y, int width, int height)
	{
		const uint SWP_NOACTIVATE = 0x10, SWP_NOZORDER = 0x4;
		Native.SetWindowPos(_window, 0, x, y, width, height, SWP_NOACTIVATE | SWP_NOZORDER);
		Controller.Bounds = new Windows.Foundation.Rect(0, 0, width, height);
	}
}

internal static class Native
{
	public delegate nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam);

	[StructLayout(LayoutKind.Sequential)]
	public struct POINT { public int X, Y; }

	[StructLayout(LayoutKind.Sequential)]
	public struct RECT { public int Left, Top, Right, Bottom; }

	[StructLayout(LayoutKind.Sequential)]
	public struct MSG { public nint hwnd; public uint message; public nint wParam, lParam; public uint time; public POINT pt; public uint lPrivate; }

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
	[DllImport("user32.dll")] public static extern bool ClientToScreen(nint hwnd, ref POINT p);
	[DllImport("user32.dll")] public static extern bool GetClientRect(nint hwnd, out RECT r);
	[DllImport("user32.dll")] public static extern uint GetDpiForWindow(nint hwnd);
	[DllImport("user32.dll")] public static extern short GetKeyState(int vk);
	[DllImport("user32.dll")] public static extern nint CreatePopupMenu();
	[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool AppendMenuW(nint menu, uint flags, nint id, string text);
	[DllImport("user32.dll")] public static extern int TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint hwnd, nint tpm);
	[DllImport("user32.dll")] public static extern bool DestroyMenu(nint menu);
	[DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern nint GetModuleHandleW(string? name);
}
