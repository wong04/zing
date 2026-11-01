using System.Runtime.InteropServices;

namespace Zing.Host;

/// <summary>The Win32 calls behind Zing's window: the window itself, its owned overlay popups, menus, keys and the cursor.</summary>
internal static class Native
{
	public delegate nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam);

	[StructLayout(LayoutKind.Sequential)]
	public struct POINT
	{
		public int X;
		public int Y;
	}

	[StructLayout(LayoutKind.Sequential)]
	public struct RECT
	{
		public int Left;
		public int Top;
		public int Right;
		public int Bottom;
	}

	[StructLayout(LayoutKind.Sequential)]
	public struct MONITORINFO
	{
		public uint Size;
		public RECT Monitor;
		public RECT Work;
		public uint Flags;
	}

	[StructLayout(LayoutKind.Sequential)]
	public struct MSG
	{
		public nint Hwnd;
		public uint Message;
		public nint WParam;
		public nint LParam;
		public uint Time;
		public POINT Point;
		public uint Private;
	}

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	public struct WNDCLASSEXW
	{
		public uint Size;
		public uint Style;
		public nint WndProc;
		public int ClassExtra;
		public int WindowExtra;
		public nint Instance;
		public nint Icon;
		public nint Cursor;
		public nint Background;
		public string? MenuName;
		public string ClassName;
		public nint SmallIcon;
	}

	public const uint WM_DESTROY = 0x0002;
	public const uint WM_MOVE = 0x0003;
	public const uint WM_SIZE = 0x0005;
	public const uint WM_ACTIVATE = 0x0006;
	public const uint WM_SETFOCUS = 0x0007;
	public const uint WM_CLOSE = 0x0010;
	public const uint WM_NCACTIVATE = 0x0086;
	public const uint WM_DPICHANGED = 0x02E0;

	public const int SW_HIDE = 0;
	public const int SW_SHOWNOACTIVATE = 4;
	public const int SW_SHOW = 5;
	public const int SW_RESTORE = 9;

	public const uint SWP_NOSIZE = 0x1;
	public const uint SWP_NOMOVE = 0x2;
	public const uint SWP_NOZORDER = 0x4;
	public const uint SWP_NOACTIVATE = 0x10;

	[DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
	public static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);

	[DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
	public static extern nint CreateWindowExW(uint exStyle, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

	[DllImport("user32.dll")]
	public static extern nint DefWindowProcW(nint hwnd, uint msg, nint wParam, nint lParam);

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool DestroyWindow(nint hwnd);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool IsWindow(nint hwnd);

	[DllImport("user32.dll")]
	public static extern int GetMessageW(out MSG msg, nint hwnd, uint min, uint max);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool TranslateMessage(ref MSG msg);

	[DllImport("user32.dll")]
	public static extern nint DispatchMessageW(ref MSG msg);

	[DllImport("user32.dll")]
	public static extern void PostQuitMessage(int code);

	[DllImport("user32.dll")]
	public static extern nint LoadCursorW(nint instance, nint name);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool SetWindowTextW(nint hwnd, string text);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool ShowWindow(nint hwnd, int command);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool IsIconic(nint hwnd);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool SetForegroundWindow(nint hwnd);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool GetClientRect(nint hwnd, out RECT rect);

	[DllImport("user32.dll")]
	public static extern uint GetDpiForWindow(nint hwnd);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool ClientToScreen(nint hwnd, ref POINT point);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool GetCursorPos(out POINT point);

	[DllImport("user32.dll")]
	public static extern nint GetForegroundWindow();

	[DllImport("user32.dll")]
	public static extern nint MonitorFromWindow(nint hwnd, uint flags);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool GetMonitorInfoW(nint monitor, ref MONITORINFO info);

	[DllImport("user32.dll")]
	public static extern nint GetFocus();

	[DllImport("user32.dll")]
	public static extern nint GetWindow(nint hwnd, uint command);

	[DllImport("user32.dll")]
	public static extern nint SendMessageW(nint hwnd, uint msg, nint wParam, nint lParam);

	[DllImport("user32.dll")]
	public static extern short GetKeyState(int virtualKey);

	[DllImport("user32.dll")]
	public static extern nint CreatePopupMenu();

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool AppendMenuW(nint menu, uint flags, nint id, string? text);

	[DllImport("user32.dll")]
	public static extern int TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint hwnd, nint parameters);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool DestroyMenu(nint menu);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
	public static extern nint GetModuleHandleW(string? name);

	[DllImport("dwmapi.dll")]
	public static extern int DwmSetWindowAttribute(nint hwnd, uint attribute, ref int value, int size);

	public static bool IsDown(int virtualKey) => (GetKeyState(virtualKey) & 0x8000) != 0;

	public static nint RegisterClass(string name, WndProc proc, nint cursor = 0)
	{
		var wc = new WNDCLASSEXW
		{
			Size = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
			WndProc = Marshal.GetFunctionPointerForDelegate(proc),
			Instance = GetModuleHandleW(null),
			Cursor = cursor,
			ClassName = name,
		};
		if (RegisterClassExW(ref wc) == 0) throw new NativeCallException($"RegisterClassEx {name}");
		return wc.Instance;
	}
}

/// <summary>A Win32 call failed; carries the system's error code.</summary>
public sealed class NativeCallException(string call) : Exception($"{call} failed: error {Marshal.GetLastWin32Error()}");
