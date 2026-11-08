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
	private static readonly Native.WndProc ChildProc = Native.DefWindowProcW;
	private static readonly Native.WndProc PopupProc = OnPopupMessage;
	private static bool _registered;

	private readonly HostWindow _window;
	private readonly nint _hwnd;
	private readonly bool _overlay;
	private readonly IDisposable _tracked;
	private Rect _bounds;
	private bool _visible;

	private HostedView(HostWindow window, nint hwnd, bool overlay, CoreWebView2Controller controller, IDisposable tracked)
	{
		_window = window;
		_hwnd = hwnd;
		_overlay = overlay;
		_tracked = tracked;
		Controller = controller;
		Live++;
		// An overlay starts hidden; a view in the window starts shown, as the engine creates it.
		Visible = !overlay;
	}

	public CoreWebView2Controller Controller { get; }

	public CoreWebView2 Core => Controller.CoreWebView2;

	public bool IsClosed { get; private set; }

	/// <summary>Views created and not yet closed, in every window (the e2e live-counts hook).</summary>
	internal static int Live { get; private set; }

	/// <summary>Views whose controller the engine is still creating, in every window.</summary>
	internal static int Pending { get; private set; }

	/// <summary>How many sibling windows are stacked above the view's window; 0 is the top.</summary>
	public int Depth => Native.Depth(_hwnd);

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
	/// <remarks>The view is tracked by <paramref name="scope"/> from the start, so a window that closes while the engine starts it closes it exactly once.</remarks>
	/// <exception cref="ViewGoneException">The window closed while the engine started the view.</exception>
	public static async Task<HostedView> CreateAsync(HostWindow window, WindowScope scope, CoreWebView2Environment environment, CoreWebView2ControllerOptions options, string name, bool overlay)
	{
		scope.ThrowIfClosed(name);
		if (window.IsClosed) throw new ViewGoneException(name);
		var hwnd = CreateHost(window.Hwnd, overlay);
		HostedView? view = null;
		var tracked = scope.Track(() => view?.Close());
		CoreWebView2Controller controller;
		Pending++;
		try
		{
			controller = await environment.CreateCoreWebView2ControllerAsync(CoreWebView2ControllerWindowReference.CreateFromWindowHandle((ulong)hwnd), options);
		}
		// Closing a window destroys its owned popups before the window's own WM_DESTROY, so an overlay still
		// starting is aborted (E_ABORT) while the window doesn't count as closed yet: its own window is gone, though.
		catch (COMException) when (window.IsClosed || scope.IsClosed || !Native.IsWindow(hwnd))
		{
			tracked.Dispose();
			throw new ViewGoneException(name);
		}
		catch
		{
			tracked.Dispose();
			throw;
		}
		finally
		{
			Pending--;
		}
		try
		{
			scope.Checkpoint(name);
			if (window.IsClosed) throw new ViewGoneException(name);
		}
		catch (ViewGoneException)
		{
			// Never adopted, so this is its only close.
			tracked.Dispose();
			controller.Close();
			throw;
		}
		// Zing's vault is the only password manager: WebView2's own saving and form autofill stay off in every view and profile, Zing's own UI included (Ruling 5).
		controller.CoreWebView2.Settings.IsPasswordAutosaveEnabled = false;
		controller.CoreWebView2.Settings.IsGeneralAutofillEnabled = false;
		if (overlay) controller.DefaultBackgroundColor = Windows.UI.Color.FromArgb(0, 255, 255, 255);
		view = new HostedView(window, hwnd, overlay, controller, tracked);
		window.Adopt(view);
		return view;
	}

	/// <summary>
	/// Forces <c>prefers-color-scheme</c> to Zing's Appearance setting. An InPrivate profile ignores
	/// PreferredColorScheme (only the persistent profile's pages obey it), so the DevTools override does the
	/// forcing; it holds across navigations. The property stays for the engine's own dialogs.
	/// </summary>
	internal void FollowTheme(string theme)
	{
		var (scheme, media) = theme switch
		{
			"light" => (CoreWebView2PreferredColorScheme.Light, "light"),
			"dark" => (CoreWebView2PreferredColorScheme.Dark, "dark"),
			"system" => (CoreWebView2PreferredColorScheme.Auto, ""),
			_ => throw new InvalidDataException($"Unknown theme {theme}"),
		};
		Core.Profile.PreferredColorScheme = scheme;
		var request = System.Text.Json.JsonSerializer.Serialize(new { features = new[] { new { name = "prefers-color-scheme", value = media } } });
		TestHooks.Background(WhileOpen(Core.CallDevToolsProtocolMethodAsync("Emulation.setEmulatedMedia", request).AsTask()), "colour scheme");
	}

	private async Task WhileOpen(Task task)
	{
		try
		{
			await task;
		}
		catch (Exception) when (IsClosed)
		{
		}
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
		EngineFocus.Move(() => Controller.MoveFocus(CoreWebView2MoveFocusReason.Programmatic));
	}

	internal bool IsOverlayWindow(nint hwnd) => _overlay && hwnd == _hwnd;

	/// <summary>Whether the keyboard is in this view right now (its window, or the engine's windows inside it).</summary>
	public bool HasKeyboard
	{
		get
		{
			if (IsClosed) return false;
			var focus = Native.GetFocus();
			return focus != 0 && (focus == _hwnd || Native.IsChild(_hwnd, focus));
		}
	}

	public void Close()
	{
		if (IsClosed) return;
		IsClosed = true;
		Live--;
		_tracked.Dispose();
		_window.Forget(this);
		try
		{
			Controller.Close();
		}
		finally
		{
			// Even when the controller's close failed, or its window would be left behind. A view's window goes with the
			// main window, so it may already be gone.
			if (!_window.IsClosed && !Native.DestroyWindow(_hwnd)) throw new NativeCallException("DestroyWindow view");
		}
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

	/// <summary>
	/// A drag on an overlay's CSS app-region:drag (the sign-in screen's top, the revealed sidebar) reaches the
	/// overlay's popup as a system command (SC_MOVE), which would move the popup off the window. An overlay has
	/// no system commands of its own, so Zing's window carries them out (move, maximise on a double-click) and
	/// the overlays follow it.
	/// </summary>
	private static nint OnPopupMessage(nint hwnd, uint msg, nint wParam, nint lParam)
	{
		const uint WM_SYSCOMMAND = 0x0112, GW_OWNER = 4;
		if (msg != WM_SYSCOMMAND) return Native.DefWindowProcW(hwnd, msg, wParam, lParam);
		return Native.SendMessageW(Native.GetWindow(hwnd, GW_OWNER), msg, wParam, lParam);
	}

	private static nint CreateHost(nint window, bool overlay)
	{
		if (!_registered)
		{
			Native.RegisterClass(ChildClass, ChildProc);
			Native.RegisterClass(PopupClass, PopupProc);
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
