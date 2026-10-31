using System.Runtime.InteropServices;

namespace Zing.Host;

/// <summary>The few Win32 calls WinUI has no API for: where the window's content is on screen, and the cursor.</summary>
internal static class Native
{
	[StructLayout(LayoutKind.Sequential)]
	public struct POINT
	{
		public int X;
		public int Y;
	}

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool ClientToScreen(nint hwnd, ref POINT point);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool GetCursorPos(out POINT point);

	[DllImport("user32.dll")]
	public static extern nint GetForegroundWindow();
}
