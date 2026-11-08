using System.ComponentModel;
using System.Runtime.InteropServices;
using Zing.Core;

namespace Zing.Host;

/// <summary>
/// The dim under a link preview: a plain Win32 owned popup with no WebView2, layered at a constant alpha, over the page
/// area. Its region is the page area minus the preview's rectangle, so the preview shows and takes clicks through the hole
/// (spikes/link-preview, check A). It never takes activation; a click on it raises <see cref="Clicked"/>.
/// </summary>
public sealed class PeekScrim : IDisposable
{
	private const string ClassName = "ZingPeekScrim";
	private const byte Alpha = 115;
	private const byte HighContrastAlpha = 160;
	private static readonly Native.WndProc Proc = Dispatch;
	private static readonly Dictionary<nint, PeekScrim> Open = [];
	private static bool _registered;

	private readonly nint _owner;
	private readonly bool _highContrast;
	private Native.RECT _hole;
	private bool _destroyed;

	/// <exception cref="NativeCallException">The window could not be created or made layered.</exception>
	public PeekScrim(nint owner, bool highContrast)
	{
		if (!_registered)
		{
			Native.RegisterClass(ClassName, Proc, Native.LoadCursorW(0, 32512));
			_registered = true;
		}
		_owner = owner;
		_highContrast = highContrast;
		const uint WS_POPUP = 0x80000000;
		// Out of the taskbar and Alt+Tab, and never the active window.
		const uint WS_EX_LAYERED = 0x80000, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
		Handle = Native.CreateWindowExW(WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, ClassName, "", WS_POPUP, 0, 0, 0, 0, owner, 0, Native.GetModuleHandleW(null), 0);
		if (Handle == 0) throw new NativeCallException("CreateWindowEx scrim");
		Open[Handle] = this;
		const uint LWA_ALPHA = 0x2;
		if (Native.SetLayeredWindowAttributes(Handle, 0, highContrast ? HighContrastAlpha : Alpha, LWA_ALPHA)) return;
		var error = new NativeCallException("SetLayeredWindowAttributes scrim");
		Dispose();
		throw error;
	}

	public nint Handle { get; }

	public event Action? Clicked;

	/// <summary>The page area it covers, in the owner's client pixels.</summary>
	public Rect PageArea { get; private set; }

	public bool Visible => !_destroyed && Native.IsWindowVisible(Handle);

	/// <summary>How many sibling windows are stacked above the scrim; 0 is the top.</summary>
	public int Depth => _destroyed ? 0 : Native.Depth(Handle);

	/// <summary>
	/// Covers <paramref name="pageAreaPx"/> except <paramref name="holePx"/>, both in the owner's client pixels, and shows.
	/// The region is rebuilt every time, so it follows a resize, a move and a DPI change. Nothing happens while the owner is
	/// minimised (Windows hides owned popups with it, and a show would bring this one back); the restore's WM_SIZE places it again.
	/// </summary>
	/// <exception cref="Win32Exception">The region could not be set.</exception>
	/// <exception cref="NativeCallException">The window could not be moved, or the region could not be made.</exception>
	public void Place(Rect pageAreaPx, Rect holePx)
	{
		if (_destroyed || Native.IsIconic(_owner)) return;
		PageArea = pageAreaPx;
		var origin = new Native.POINT();
		Native.ClientToScreen(_owner, ref origin);
		var left = (int)pageAreaPx.X;
		var top = (int)pageAreaPx.Y;
		var width = (int)pageAreaPx.Width;
		var height = (int)pageAreaPx.Height;
		if (!Native.SetWindowPos(Handle, 0, left + origin.X, top + origin.Y, width, height, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE)) throw new NativeCallException("SetWindowPos scrim");
		_hole = new Native.RECT
		{
			Left = (int)holePx.X - left,
			Top = (int)holePx.Y - top,
			Right = (int)(holePx.X + holePx.Width) - left,
			Bottom = (int)(holePx.Y + holePx.Height) - top,
		};
		var region = HoledRegion(width, height);
		// On success the window owns the region; on failure it is still the caller's.
		if (Native.SetWindowRgn(Handle, region, true) == 0)
		{
			var code = Marshal.GetLastWin32Error();
			Native.DeleteObject(region);
			throw new Win32Exception(code, "SetWindowRgn scrim");
		}
		Native.InvalidateRect(Handle, 0, true);
		Native.ShowWindow(Handle, Native.SW_SHOWNOACTIVATE);
	}

	/// <summary>
	/// The page area minus the hole, in the scrim's own coordinates. A failed region must never be set: a null region would
	/// drop the hole, and the scrim would take every click meant for the preview.
	/// </summary>
	private nint HoledRegion(int width, int height)
	{
		var region = Native.CreateRectRgn(0, 0, width, height);
		if (region == 0) throw new NativeCallException("CreateRectRgn scrim");
		var hole = Native.CreateRectRgn(_hole.Left, _hole.Top, _hole.Right, _hole.Bottom);
		if (hole == 0)
		{
			Native.DeleteObject(region);
			throw new NativeCallException("CreateRectRgn scrim hole");
		}
		const int RGN_DIFF = 4, ERROR = 0;
		var combined = Native.CombineRgn(region, region, hole, RGN_DIFF);
		Native.DeleteObject(hole);
		if (combined != ERROR) return region;
		Native.DeleteObject(region);
		throw new NativeCallException("CombineRgn scrim");
	}

	/// <summary>Takes the scrim off the screen until the next <see cref="Place"/>: the preview's page has the whole window.</summary>
	public void Hide()
	{
		if (!_destroyed) Native.ShowWindow(Handle, Native.SW_HIDE);
	}

	/// <summary>Puts the scrim above the window's other popups; OverlayHost re-raises the ones that sit above it.</summary>
	public void Raise()
	{
		if (!_destroyed) Native.SetWindowPos(Handle, 0, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
	}

	/// <summary>Destroys the window, unless it already went with its owner.</summary>
	public void Dispose()
	{
		if (_destroyed) return;
		if (!Native.DestroyWindow(Handle)) throw new NativeCallException("DestroyWindow scrim");
	}

	/// <summary>
	/// The dim, and a ring just outside the hole: 1 px, 2 px under high contrast, in the system's highlight colour there.
	/// Everything sits under the window's one constant alpha.
	/// </summary>
	private void Paint()
	{
		var dc = Native.BeginPaint(Handle, out var paint);
		try
		{
			Native.GetClientRect(Handle, out var client);
			const int BLACK_BRUSH = 4;
			Native.FillRect(dc, ref client, Native.GetStockObject(BLACK_BRUSH));
			var width = _highContrast ? 2 : 1;
			const int COLOR_HIGHLIGHT = 13;
			var brush = Native.CreateSolidBrush(_highContrast ? Native.GetSysColor(COLOR_HIGHLIGHT) : 0x00FFFFFF);
			if (brush == 0) throw new NativeCallException("CreateSolidBrush scrim border");
			try
			{
				// The hole itself is outside the window's region, so filling the grown rectangle draws only the ring.
				var ring = new Native.RECT { Left = _hole.Left - width, Top = _hole.Top - width, Right = _hole.Right + width, Bottom = _hole.Bottom + width };
				Native.FillRect(dc, ref ring, brush);
			}
			finally
			{
				Native.DeleteObject(brush);
			}
		}
		finally
		{
			Native.EndPaint(Handle, ref paint);
		}
	}

	private static nint Dispatch(nint hwnd, uint msg, nint wParam, nint lParam)
	{
		const uint WM_PAINT = 0x000F, WM_ERASEBKGND = 0x0014, WM_MOUSEACTIVATE = 0x0021, WM_LBUTTONDOWN = 0x0201;
		const int MA_NOACTIVATE = 3;
		if (!Open.TryGetValue(hwnd, out var scrim)) return Native.DefWindowProcW(hwnd, msg, wParam, lParam);
		switch (msg)
		{
			case WM_MOUSEACTIVATE:
				return MA_NOACTIVATE;
			case WM_LBUTTONDOWN:
				TestHooks.Guard(() => scrim.Clicked?.Invoke(), "preview scrim click");
				return 0;
			case WM_ERASEBKGND:
				// WM_PAINT draws all of it.
				return 1;
			case WM_PAINT:
				TestHooks.Guard(scrim.Paint, "preview scrim paint");
				return 0;
			case Native.WM_DESTROY:
				scrim._destroyed = true;
				Open.Remove(hwnd);
				break;
		}
		return Native.DefWindowProcW(hwnd, msg, wParam, lParam);
	}
}
