using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using Zing.Core;

namespace Zing.Host;

/// <summary>
/// A WebView2 controller the window hosts itself, in a window of Zing's own so Zing decides its place and
/// stacking (the engine's own windows can't be ordered). A view in the window (chrome, pages) sits in a
/// child window. An overlay (notices, page UI, the surface, covering layers) sits in an owned popup: a
/// transparent WebView2 only shows what is beneath when that is another window, and owned popups always
/// stay above their owner.
/// </summary>
public sealed class HostedView
{
	private const string ChildClass = "ZingView";
	private const string PopupClass = "ZingOverlay";
	private static readonly Native.WndProc Proc = Native.DefWindowProcW;
	private static bool _registered;

	private readonly HostWindow _window;
	private readonly nint _hwnd;
	private readonly bool _overlay;
	private Rect _bounds;
	private bool _visible;

	private HostedView(HostWindow window, nint hwnd, bool overlay, CoreWebView2Controller controller)
	{
		_window = window;
		_hwnd = hwnd;
		_overlay = overlay;
		Controller = controller;
		// An overlay starts hidden; a view in the window starts shown, as the engine creates it.
		Visible = !overlay;
	}

	public CoreWebView2Controller Controller { get; }

	public CoreWebView2 Core => Controller.CoreWebView2;

	public bool IsClosed { get; private set; }

	/// <summary>How many sibling windows are stacked above the view's window; 0 is the top.</summary>
	public int Depth
	{
		get
		{
			const uint GW_HWNDPREV = 3;
			var depth = 0;
			for (var above = Native.GetWindow(_hwnd, GW_HWNDPREV); above != 0; above = Native.GetWindow(above, GW_HWNDPREV)) depth++;
			return depth;
		}
	}

	/// <summary>Whether the view floats above the window in a popup of its own.</summary>
	public bool IsOverlay => _overlay;

	/// <summary>Where the view is in the window's content, in DIPs.</summary>
	public Rect Bounds => _bounds;

	public bool Visible
	{
		get => _visible;
		set
		{
			if (IsClosed) return;
			_visible = value;
			Controller.IsVisible = value;
			Native.ShowWindow(_hwnd, value ? Native.SW_SHOWNOACTIVATE : Native.SW_HIDE);
		}
	}

	/// <summary>Creates a view in <paramref name="window"/>; <paramref name="overlay"/> views are transparent and float above it.</summary>
	/// <exception cref="ViewGoneException">The window closed while the engine started the view.</exception>
	public static async Task<HostedView> CreateAsync(HostWindow window, CoreWebView2Environment environment, CoreWebView2ControllerOptions options, string name, bool overlay)
	{
		if (window.IsClosed) throw new ViewGoneException(name);
		var hwnd = CreateHost(window.Hwnd, overlay);
		CoreWebView2Controller controller;
		try
		{
			controller = await environment.CreateCoreWebView2ControllerAsync(CoreWebView2ControllerWindowReference.CreateFromWindowHandle((ulong)hwnd), options);
		}
		catch (COMException) when (window.IsClosed)
		{
			throw new ViewGoneException(name);
		}
		if (window.IsClosed)
		{
			controller.Close();
			throw new ViewGoneException(name);
		}
		if (overlay) controller.DefaultBackgroundColor = Windows.UI.Color.FromArgb(0, 255, 255, 255);
		var view = new HostedView(window, hwnd, overlay, controller);
		window.Adopt(view);
		return view;
	}

	/// <summary>Places the view at a rectangle of the window's content, in DIPs.</summary>
	public void Place(Rect bounds)
	{
		_bounds = bounds;
		Apply();
	}

	/// <summary>Keeps an overlay over the window when the window moves, and tells the engine where it is now.</summary>
	internal void WindowMoved()
	{
		if (IsClosed) return;
		if (_overlay) Apply();
		Controller.NotifyParentWindowPositionChanged();
	}

	/// <summary>Puts the view above its siblings: an overlay above the other overlays.</summary>
	public void Raise() => Stack(insertAfter: 0);

	/// <summary>Puts the view under every other view of the window, as the chrome is under the pages.</summary>
	public void SendToBack() => Stack(insertAfter: 1);

	public void Focus()
	{
		if (IsClosed) return;
		// Keys only go to the active window, and an overlay is a window of its own: activate it, but only
		// while Zing is in front, so a background window (or a test run) never takes focus from another app.
		if (_overlay && _window.IsForeground) Native.SetForegroundWindow(_hwnd);
		Controller.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);
	}

	internal bool IsOverlayWindow(nint hwnd) => _overlay && hwnd == _hwnd;

	public void Close()
	{
		if (IsClosed) return;
		IsClosed = true;
		_window.Forget(this);
		Controller.Close();
		// A view's window goes with the main window, so it may already be gone.
		if (!_window.IsClosed && !Native.DestroyWindow(_hwnd)) throw new NativeCallException("DestroyWindow view");
	}

	private void Stack(nint insertAfter)
	{
		if (!IsClosed) Native.SetWindowPos(_hwnd, insertAfter, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
	}

	private void Apply()
	{
		if (IsClosed) return;
		var scale = _window.Scale;
		var width = (int)Math.Round(Math.Max(0, _bounds.Width) * scale);
		var height = (int)Math.Round(Math.Max(0, _bounds.Height) * scale);
		var x = (int)Math.Round(_bounds.X * scale);
		var y = (int)Math.Round(_bounds.Y * scale);
		if (_overlay)
		{
			// A popup is placed on screen.
			var origin = _window.ClientOrigin;
			x += origin.X;
			y += origin.Y;
		}
		Native.SetWindowPos(_hwnd, 0, x, y, width, height, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
		Controller.Bounds = new Windows.Foundation.Rect(0, 0, width, height);
	}

	private static nint CreateHost(nint window, bool overlay)
	{
		if (!_registered)
		{
			Native.RegisterClass(ChildClass, Proc);
			Native.RegisterClass(PopupClass, Proc);
			_registered = true;
		}
		const uint WS_CHILD = 0x40000000, WS_POPUP = 0x80000000, WS_CLIPSIBLINGS = 0x04000000, WS_CLIPCHILDREN = 0x02000000;
		// Out of the taskbar and Alt+Tab; no redirection bitmap, so the engine's transparent pixels show the window beneath.
		const uint WS_EX_TOOLWINDOW = 0x80, WS_EX_NOREDIRECTIONBITMAP = 0x200000;
		var hwnd = overlay
			? Native.CreateWindowExW(WS_EX_TOOLWINDOW | WS_EX_NOREDIRECTIONBITMAP, PopupClass, "", WS_POPUP, 0, 0, 0, 0, window, 0, Native.GetModuleHandleW(null), 0)
			: Native.CreateWindowExW(0, ChildClass, "", WS_CHILD | WS_CLIPSIBLINGS | WS_CLIPCHILDREN, 0, 0, 0, 0, window, 0, Native.GetModuleHandleW(null), 0);
		if (hwnd == 0) throw new NativeCallException(overlay ? "CreateWindowEx overlay" : "CreateWindowEx view");
		return hwnd;
	}
}
