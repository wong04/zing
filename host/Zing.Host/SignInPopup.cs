using Microsoft.Web.WebView2.Core;
using Zing.Core.Sync;

namespace Zing.Host;

/// <summary>
/// Google's sign-in page for sync in a small window of Zing's own, owned by the Zing window used last: about 500 by 650 DIPs,
/// centred over it, with a taskbar entry so Alt+Tab reaches it. It has no tabs or toolbar, only the address, read-only, along the
/// top, so the person can see it's Google's. Its WebView2 is the one tabs use, the same environment and browsing profile
/// (WebView2Page.BrowsingProfile), so a Google sign-in already in Zing is reused, with the engine's own user agent. Closing it
/// before Google sends the person back cancels the sign-in (GoogleSignIn). In an e2e run it opens off-screen, out of the
/// switchers, and never takes focus (AGENTS.md).
/// </summary>
public sealed class SignInPopup : ISignInPopup
{
	private const string ClassName = "ZingSignIn";
	private const int Width = 500;
	private const int Height = 650;
	/// <summary>The address bar's height, in DIPs.</summary>
	private const int AddressHeight = 30;
	private static readonly Native.WndProc Proc = Dispatch;
	private static readonly Dictionary<nint, SignInPopup> Open = [];
	private static bool _registered;

	private readonly nint _hwnd;
	private readonly nint _address;
	private readonly ZingApp _app;
	private readonly MainWindow _owner;
	/// <summary>Whether Zing was minimised when the window opened, so handing back never restores a window the person minimised.</summary>
	private readonly bool _ownerWasMinimised;
	private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private CoreWebView2Controller? _controller;
	private nint _font;
	/// <summary>Why the window couldn't show Google's page, which <see cref="Closed"/> faults with once it's gone.</summary>
	private SignInWindowFailedException? _failure;

	private SignInPopup(ZingApp app, MainWindow owner, Uri authorize)
	{
		_app = app;
		_owner = owner;
		_ownerWasMinimised = Native.IsIconic(owner.Hwnd);
		if (!_registered)
		{
			Native.RegisterClass(ClassName, Proc, Native.LoadCursorW(0, 32512));
			_registered = true;
		}
		const uint WS_OVERLAPPED = 0, WS_CAPTION = 0x00C00000, WS_SYSMENU = 0x00080000, WS_THICKFRAME = 0x00040000, WS_MINIMIZEBOX = 0x00020000, WS_CLIPCHILDREN = 0x02000000;
		// An owned window has no taskbar entry of its own unless it asks for one; an e2e run's stays out of the switchers.
		const uint WS_EX_APPWINDOW = 0x40000, WS_EX_TOOLWINDOW = 0x80;
		var exStyle = TestHooks.KeepsOutOfTheWay ? WS_EX_TOOLWINDOW : WS_EX_APPWINDOW;
		// Created where it goes, at the owner's DPI, so it starts on the owner's monitor and no DPI change rescales it.
		var (x, y, width, height) = Bounds(owner.Hwnd);
		_hwnd = Native.CreateWindowExW(exStyle, ClassName, "Sign in with Google - Zing", WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_THICKFRAME | WS_MINIMIZEBOX | WS_CLIPCHILDREN, x, y, width, height, owner.Hwnd, 0, Native.GetModuleHandleW(null), 0);
		if (_hwnd == 0) throw new NativeCallException("CreateWindowEx sign-in");
		Open[_hwnd] = this;
		try
		{
			const uint WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, ES_AUTOHSCROLL = 0x80, ES_READONLY = 0x800;
			_address = Native.CreateWindowExW(0, "EDIT", authorize.AbsoluteUri, WS_CHILD | WS_VISIBLE | ES_AUTOHSCROLL | ES_READONLY, 0, 0, 0, 0, _hwnd, 0, Native.GetModuleHandleW(null), 0);
			if (_address == 0) throw new NativeCallException("CreateWindowEx sign-in address");
			Address = authorize.AbsoluteUri;
			UseFont();
			Layout();
		}
		catch
		{
			// Gone with its WM_DESTROY, which also takes it out of Open.
			Native.DestroyWindow(_hwnd);
			throw;
		}
		Native.ShowWindow(_hwnd, TestHooks.KeepsOutOfTheWay ? Native.SW_SHOWNOACTIVATE : Native.SW_SHOW);
		if (!TestHooks.KeepsOutOfTheWay) Native.SetForegroundWindow(_hwnd);
		TestHooks.Background(StartAsync(authorize), "sign-in window");
	}

	/// <summary>The one open, for the e2e sign-in-window hook; null when none is.</summary>
	internal static SignInPopup? Current => Open.Values.SingleOrDefault();

	/// <summary>Opens the window over the Zing window used last. On the UI thread.</summary>
	/// <exception cref="InvalidOperationException">No Zing window is open to own it.</exception>
	public static SignInPopup OpenOver(ZingApp app, Uri authorize) =>
		new(app, app.MostRecent ?? throw new InvalidOperationException("Google sign-in needs a Zing window to open over"), authorize);

	public Task Closed => _closed.Task;

	/// <summary>The address shown along the top.</summary>
	internal string Address { get; private set; } = "";

	/// <summary>The window that owns it (GW_OWNER).</summary>
	internal nint Owner => Native.GetWindow(_hwnd, 4);

	/// <summary>The window's size in DIPs.</summary>
	internal (double Width, double Height) Size
	{
		get
		{
			if (!Native.GetWindowRect(_hwnd, out var rect)) throw new NativeCallException("GetWindowRect sign-in");
			var scale = Native.GetDpiForWindow(_hwnd) / 96.0;
			return ((rect.Right - rect.Left) / scale, (rect.Bottom - rect.Top) / scale);
		}
	}

	public void BringToFront()
	{
		if (_closed.Task.IsCompleted) return;
		if (Native.IsIconic(_hwnd)) Native.ShowWindow(_hwnd, Native.SW_RESTORE);
		if (TestHooks.KeepsOutOfTheWay)
		{
			Native.SetWindowPos(_hwnd, 0, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
			return;
		}
		Native.SetForegroundWindow(_hwnd);
	}

	public void Close()
	{
		if (_closed.Task.IsCompleted) return;
		HandBackToOwner();
		if (!Native.DestroyWindow(_hwnd)) throw new NativeCallException("DestroyWindow sign-in");
	}

	/// <summary>
	/// Brings Zing back to the front before this window goes. Destroying an active owned window lets Windows pick the next window
	/// itself, which can be another app, so Zing looked minimised after a sign-in.
	/// </summary>
	private void HandBackToOwner()
	{
		if (TestHooks.KeepsOutOfTheWay) return;
		if (Native.GetForegroundWindow() != _hwnd) return;
		var owner = _owner.Hwnd;
		if (!_ownerWasMinimised && Native.IsIconic(owner)) Native.ShowWindow(owner, Native.SW_RESTORE);
		Native.SetForegroundWindow(owner);
	}

	/// <summary>As the person's click on the close button does (the e2e sign-in-window hook).</summary>
	internal void CloseAsThePersonWould() => Native.SendMessageW(_hwnd, Native.WM_CLOSE, 0, 0);

	private async Task StartAsync(Uri authorize)
	{
		try
		{
			var environment = await _app.Environment;
			if (_closed.Task.IsCompleted) return;
			var controller = await environment.CreateCoreWebView2ControllerAsync(CoreWebView2ControllerWindowReference.CreateFromWindowHandle((ulong)_hwnd), WebView2Page.BrowsingProfile(environment, _app.PersistentProfile));
			if (_closed.Task.IsCompleted)
			{
				controller.Close();
				return;
			}
			_controller = controller;
			var core = controller.CoreWebView2;
			// As in every view (HostedView): Zing's vault is the only password manager (Ruling 5). The page is Google's, not Zing's UI.
			core.Settings.IsPasswordAutosaveEnabled = false;
			core.Settings.IsGeneralAutofillEnabled = false;
			core.Settings.AreHostObjectsAllowed = false;
			core.Settings.IsWebMessageEnabled = false;
			core.Settings.AreDevToolsEnabled = TestHooks.IsEndToEndRun;
			core.SourceChanged += (_, _) => ShowAddress(core.Source);
			// A sign-in has nothing to download or ask permission for; neither goes around Zing's downloads list and site permissions.
			core.DownloadStarting += (_, e) => e.Cancel = true;
			core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
			core.ProcessFailed += (_, e) => TestHooks.Guard(() => OnProcessFailed(e.ProcessFailedKind), "sign-in window engine");
			// A link the person clicks that opens a window (Google's help, its privacy policy) opens as a tab of the Zing window under
			// this one, by the rules tabs follow; nothing else opens (SignInWindowLinks).
			core.NewWindowRequested += (_, e) =>
			{
				e.Handled = true;
				if (SignInWindowLinks.TabFor(core.Source, e.Uri, e.IsUserInitiated) is not { } tab) return;
				TestHooks.Guard(() => (_owner.Api ?? throw new InvalidOperationException("The Zing window under the sign-in window has no tabs yet")).Tabs.Open(tab), "sign-in window link");
			};
			Layout();
			core.Navigate(authorize.AbsoluteUri);
		}
		catch (Exception) when (_closed.Task.IsCompleted)
		{
			// Closed while the engine started it (by the person, or with Zing's window): gone, not broken.
			throw new ViewGoneException("sign-in window");
		}
		catch (Exception error)
		{
			// An empty window would wait for a redirect that never comes: the sign-in ends with this error, which Settings shows.
			Fail(error);
			throw;
		}
	}

	/// <summary>The page's engine process ended: the window can't finish the sign-in, which ends with an error.</summary>
	private void OnProcessFailed(CoreWebView2ProcessFailedKind kind)
	{
		if (kind is not (CoreWebView2ProcessFailedKind.BrowserProcessExited or CoreWebView2ProcessFailedKind.RenderProcessExited)) return;
		Fail(new InvalidOperationException($"The sign-in window's engine process ended ({kind})"));
	}

	/// <summary>Closes the window, and <see cref="Closed"/> faults with why.</summary>
	private void Fail(Exception error)
	{
		_failure ??= new SignInWindowFailedException(error);
		Close();
	}

	private void ShowAddress(string url)
	{
		Address = url;
		Native.SetWindowTextW(_address, url);
	}

	/// <summary>The address font, Segoe UI at 9 points for the window's DPI.</summary>
	private void UseFont()
	{
		const uint WM_SETFONT = 0x0030, DEFAULT_CHARSET = 1, CLEARTYPE_QUALITY = 5;
		const int FW_NORMAL = 400;
		var font = Native.CreateFontW(-(int)Math.Round(9 * Native.GetDpiForWindow(_hwnd) / 72.0), 0, 0, 0, FW_NORMAL, 0, 0, 0, DEFAULT_CHARSET, 0, 0, CLEARTYPE_QUALITY, 0, "Segoe UI");
		if (font == 0) throw new NativeCallException("CreateFont sign-in address");
		Native.SendMessageW(_address, WM_SETFONT, font, 1);
		if (_font != 0) Native.DeleteObject(_font);
		_font = font;
	}

	/// <summary>Centred over the owner at its DPI, or off-screen in an e2e run, in screen pixels.</summary>
	private static (int X, int Y, int Width, int Height) Bounds(nint ownerHwnd)
	{
		if (!Native.GetWindowRect(ownerHwnd, out var owner)) throw new NativeCallException("GetWindowRect sign-in owner");
		var scale = Native.GetDpiForWindow(ownerHwnd) / 96.0;
		var width = (int)Math.Round(Width * scale);
		var height = (int)Math.Round(Height * scale);
		var x = TestHooks.KeepsOutOfTheWay ? -20000 : owner.Left + (owner.Right - owner.Left - width) / 2;
		var y = TestHooks.KeepsOutOfTheWay ? -20000 : owner.Top + (owner.Bottom - owner.Top - height) / 2;
		return (x, y, width, height);
	}

	/// <summary>The address along the top, the page under it, in the window's client pixels.</summary>
	private void Layout()
	{
		Native.GetClientRect(_hwnd, out var client);
		var bar = (int)Math.Round(AddressHeight * Native.GetDpiForWindow(_hwnd) / 96.0);
		Native.SetWindowPos(_address, 0, 0, 0, client.Right, bar, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
		if (_controller is { } controller) controller.Bounds = new Windows.Foundation.Rect(0, bar, client.Right, Math.Max(0, client.Bottom - bar));
	}

	private static nint Dispatch(nint hwnd, uint msg, nint wParam, nint lParam)
	{
		if (!Open.TryGetValue(hwnd, out var popup)) return Native.DefWindowProcW(hwnd, msg, wParam, lParam);
		switch (msg)
		{
			case Native.WM_SIZE:
				TestHooks.Guard(popup.Layout, "sign-in window resized");
				break;
			case Native.WM_MOVE:
				popup._controller?.NotifyParentWindowPositionChanged();
				break;
			case Native.WM_SETFOCUS:
				if (popup._controller is { } focused) TestHooks.Guard(() => focused.MoveFocus(CoreWebView2MoveFocusReason.Programmatic), "sign-in window focus");
				break;
			case Native.WM_DPICHANGED:
				var suggested = System.Runtime.InteropServices.Marshal.PtrToStructure<Native.RECT>(lParam);
				Native.SetWindowPos(hwnd, 0, suggested.Left, suggested.Top, suggested.Right - suggested.Left, suggested.Bottom - suggested.Top, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
				TestHooks.Guard(popup.UseFont, "sign-in window dpi changed");
				TestHooks.Guard(popup.Layout, "sign-in window dpi changed");
				return 0;
			case Native.WM_CLOSE:
				TestHooks.Guard(popup.HandBackToOwner, "sign-in window closing");
				break;
			case Native.WM_DESTROY:
				Open.Remove(hwnd);
				TestHooks.Guard(popup.Destroyed, "sign-in window closed");
				return 0;
		}
		return Native.DefWindowProcW(hwnd, msg, wParam, lParam);
	}

	private void Destroyed()
	{
		try
		{
			_controller?.Close();
		}
		finally
		{
			_controller = null;
			if (_font != 0) Native.DeleteObject(_font);
			_font = 0;
			if (_failure is { } failure) _closed.TrySetException(failure);
			else _closed.TrySetResult();
		}
	}
}
