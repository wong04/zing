using Microsoft.Web.WebView2.Core;
using Zing.Core;

namespace Zing.Host;

/// <summary>
/// A plain Win32 top-level window that owns every WebView2 of a Zing window, the pattern of Microsoft's
/// WebView2Browser sample (CreateWindowW + CreateCoreWebView2Controller on it). A WinUI window put its own
/// content window in there too, which took keyboard focus on every activation, so keys never reached the
/// views. WinUI's windowing API still draws the title bar: Windows' caption buttons, Snap Layouts included.
/// </summary>
public sealed class HostWindow
{
	private const string ClassName = "ZingWindow";
	private static readonly Native.WndProc Proc = Dispatch;
	private static readonly Dictionary<nint, HostWindow> Open = [];
	private static nint _instance;

	private readonly List<HostedView> _views = [];
	private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
	private HostedView? _focused;
	private HostedView? _backmost;
	private string? _theme;

	public HostWindow(string title, int width, int height)
	{
		if (_instance == 0) _instance = Native.RegisterClass(ClassName, Proc, Native.LoadCursorW(0, 32512));
		const uint WS_OVERLAPPEDWINDOW = 0x00CF0000, WS_CLIPCHILDREN = 0x02000000;
		const int CW_USEDEFAULT = unchecked((int)0x80000000);
		Hwnd = Native.CreateWindowExW(0, ClassName, title, WS_OVERLAPPEDWINDOW | WS_CLIPCHILDREN, CW_USEDEFAULT, CW_USEDEFAULT, 0, 0, 0, 0, _instance, 0);
		if (Hwnd == 0) throw new NativeCallException("CreateWindowEx");
		Open[Hwnd] = this;
		AppWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(Hwnd));
		AppWindow.Resize(FitToScreen(width, height));
		AppWindow.TitleBar.ExtendsContentIntoTitleBar = true;
		AppWindow.TitleBar.PreferredHeightOption = Microsoft.UI.Windowing.TitleBarHeightOption.Tall;
		PassThroughTitleBar();
	}

	public nint Hwnd { get; }

	/// <summary>Windows created and not yet destroyed (the e2e live-counts hook).</summary>
	internal static int OpenCount => Open.Count;

	public Microsoft.UI.Windowing.AppWindow AppWindow { get; }

	public bool IsClosed { get; private set; }

	/// <summary>The window's pixels per DIP.</summary>
	public double Scale => Native.GetDpiForWindow(Hwnd) / 96.0;

	/// <summary>Every view of the window in creation order, for the e2e layers hook.</summary>
	public IReadOnlyList<HostedView> Views => _views;

	public event Action? Resized;
	public event Action? Activated;
	public event Action<HostedView, CoreWebView2AcceleratorKeyPressedEventArgs>? KeyPressed;
	public event Action? Destroyed;

	/// <summary>The view that takes the keyboard when the window has it and no view did before (the shown page, else the chrome).</summary>
	public Func<HostedView?> DefaultFocus { get; set; } = () => null;

	/// <summary>
	/// The view kept under all others (the chrome, which fills the window). A new child window doesn't land
	/// above the existing ones, so a page started later (a reopened tab) went under the chrome and showed blank.
	/// </summary>
	public HostedView? Backmost
	{
		get => _backmost;
		set
		{
			_backmost = value;
			value?.SendToBack();
		}
	}

	/// <summary>The Appearance setting, which every view of the window follows, now and from its creation. Set before the first view.</summary>
	public string Theme
	{
		get => _theme ?? throw new InvalidOperationException("The window has no theme yet");
		set
		{
			_theme = value;
			foreach (var view in _views) view.FollowTheme(value);
		}
	}

	/// <summary>The client area in DIPs.</summary>
	public ContentSize ClientSize
	{
		get
		{
			Native.GetClientRect(Hwnd, out var rect);
			var scale = Scale;
			return new ContentSize(rect.Right / scale, rect.Bottom / scale);
		}
	}

	/// <summary>The client area's top-left on screen, in pixels.</summary>
	internal Native.POINT ClientOrigin
	{
		get
		{
			var origin = new Native.POINT();
			Native.ClientToScreen(Hwnd, ref origin);
			return origin;
		}
	}

	/// <summary>Shows the window; test runs never take focus (AGENTS.md).</summary>
	public void Show(bool activate)
	{
		Native.ShowWindow(Hwnd, activate ? Native.SW_SHOW : Native.SW_SHOWNOACTIVATE);
		if (activate) Native.SetForegroundWindow(Hwnd);
	}

	public void Restore()
	{
		if (Native.IsIconic(Hwnd)) Native.ShowWindow(Hwnd, Native.SW_RESTORE);
	}

	public void Close()
	{
		if (!IsClosed && !Native.DestroyWindow(Hwnd)) throw new NativeCallException("DestroyWindow");
	}

	internal void Adopt(HostedView view)
	{
		_views.Add(view);
		view.FollowTheme(Theme);
		if (!view.IsOverlay) _backmost?.SendToBack();
		view.Controller.GotFocus += (_, _) => _focused = view;
		view.Controller.AcceleratorKeyPressed += (_, e) => KeyPressed?.Invoke(view, e);
	}

	internal void Forget(HostedView view)
	{
		_views.Remove(view);
		if (_focused == view) _focused = null;
		if (_backmost == view) _backmost = null;
	}

	private static nint Dispatch(nint hwnd, uint msg, nint wParam, nint lParam)
	{
		if (!Open.TryGetValue(hwnd, out var window)) return Native.DefWindowProcW(hwnd, msg, wParam, lParam);
		switch (msg)
		{
			case Native.WM_SIZE:
				const int SIZE_MINIMIZED = 1;
				if (wParam == SIZE_MINIMIZED) break;
				window.PassThroughTitleBar();
				window.Resized?.Invoke();
				break;
			case Native.WM_NCACTIVATE:
				// An overlay taking the keyboard (the find box, Settings) is still this window: keep its frame active.
				if (wParam == 0 && window.IsOverlay(lParam)) return Native.DefWindowProcW(hwnd, msg, 1, lParam);
				break;
			case Native.WM_MOVE:
				// Owned popups are placed on screen, so they follow the window by hand; the engine needs to know too.
				foreach (var view in window._views) view.WindowMoved();
				break;
			case Native.WM_ACTIVATE:
				const int WA_INACTIVE = 0;
				if ((wParam & 0xFFFF) != WA_INACTIVE) window.Activated?.Invoke();
				break;
			case Native.WM_SETFOCUS:
				// After the queued messages, so a click that activated the window places focus itself first.
				Faults.Enqueue(window._dispatcher, window.PassFocus, "passing focus");
				break;
			case Native.WM_DPICHANGED:
				var suggested = System.Runtime.InteropServices.Marshal.PtrToStructure<Native.RECT>(lParam);
				Native.SetWindowPos(hwnd, 0, suggested.Left, suggested.Top, suggested.Right - suggested.Left, suggested.Bottom - suggested.Top, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
				return 0;
			case Native.WM_DESTROY:
				window.IsClosed = true;
				Open.Remove(hwnd);
				window.Destroyed?.Invoke();
				return 0;
		}
		return Native.DefWindowProcW(hwnd, msg, wParam, lParam);
	}

	/// <summary>
	/// WinUI's title-bar input window covers the top band of the window and answers "caption" there even
	/// with no caption rectangles, which took the tabs' clicks. So the band passes through to the chrome view,
	/// whose CSS app-region:drag areas move the window, except where Windows draws the caption buttons: a
	/// pass-through rectangle over them sent their clicks to the chrome too, and minimise, maximise and close
	/// did nothing (only the drag region's double-click worked). The rectangles are in client pixels.
	/// </summary>
	private void PassThroughTitleBar()
	{
		Native.GetClientRect(Hwnd, out var client);
		var bar = AppWindow.TitleBar;
		var buttons = Math.Clamp(bar.RightInset, 0, client.Right);
		var band = Math.Clamp(bar.Height, 0, client.Bottom);
		Windows.Graphics.RectInt32[] passThrough = buttons == 0 || band == 0
			? [new(0, 0, client.Right, client.Bottom)]
			: [new(0, 0, client.Right - buttons, band), new(0, band, client.Right, client.Bottom - band)];
		Microsoft.UI.Input.InputNonClientPointerSource.GetForWindowId(AppWindow.Id).SetRegionRects(Microsoft.UI.Input.NonClientRegionKind.Passthrough, passThrough);
	}

	/// <summary>A size in DIPs, in pixels for this window's screen, at most 90% of its work area so it opens whole.</summary>
	private Windows.Graphics.SizeInt32 FitToScreen(int width, int height)
	{
		const uint MONITOR_DEFAULTTONEAREST = 2;
		var info = new Native.MONITORINFO { Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
		if (!Native.GetMonitorInfoW(Native.MonitorFromWindow(Hwnd, MONITOR_DEFAULTTONEAREST), ref info)) throw new NativeCallException("GetMonitorInfo");
		var work = info.Work;
		return new Windows.Graphics.SizeInt32(
			(int)Math.Min(width * Scale, (work.Right - work.Left) * 0.9),
			(int)Math.Min(height * Scale, (work.Bottom - work.Top) * 0.9));
	}

	/// <summary>Whether this window or one of its overlays is the one in front.</summary>
	internal bool IsForeground
	{
		get
		{
			var front = Native.GetForegroundWindow();
			return front == Hwnd || IsOverlay(front);
		}
	}

	internal bool IsOverlay(nint hwnd) => hwnd != 0 && _views.Any(v => v.IsOverlayWindow(hwnd));

	/// <summary>
	/// The window itself never keeps the keyboard: keys there reach no view, so no shortcut works. It gets it
	/// on activation and when the focused view closes (Ctrl+W); like a dialog handing focus to a control, it
	/// passes it on to the view that had it, else the default one.
	/// </summary>
	/// <summary>
	/// The page that last had the keyboard isn't the one in front any more (another tab, or another pane of a split, became
	/// active): when the window gets the keyboard back, it goes to <see cref="DefaultFocus"/> rather than to that page.
	/// </summary>
	public void ForgetFocusedPage(Func<HostedView, bool> isPage, HostedView? front)
	{
		if (_focused is { } last && last != front && isPage(last)) _focused = null;
	}

	private void PassFocus()
	{
		if (IsClosed || Native.GetFocus() != Hwnd) return;
		var target = _focused is { IsClosed: false, Visible: true } last ? last : DefaultFocus();
		target?.Focus();
	}

}

