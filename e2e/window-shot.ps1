# Draws the screen area of the Zing window of the process -ProcessId into the PNG -Out (e2e/link-preview-screenshots.spec.ts).
# A page's DevTools screenshot covers one view; the window is the only picture with the preview, its dim and its buttons together.
param(
	[Parameter(Mandatory = $true)][int]$ProcessId,
	[Parameter(Mandatory = $true)][string]$Out
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class ZingWindowShot
{
	private delegate bool EnumProc(IntPtr hwnd, IntPtr data);
	[StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
	[DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc proc, IntPtr data);
	[DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
	[DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr hwnd, StringBuilder name, int size);
	[DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
	[DllImport("user32.dll")] private static extern bool SetProcessDPIAware();
	[DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out Rect rect, int size);

	private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

	public static Rect Bounds(int processId)
	{
		SetProcessDPIAware();
		var found = IntPtr.Zero;
		EnumWindows((hwnd, _) =>
		{
			uint pid;
			GetWindowThreadProcessId(hwnd, out pid);
			if (pid != processId) return true;
			var name = new StringBuilder(64);
			GetClassNameW(hwnd, name, name.Capacity);
			if (name.ToString() != "ZingWindow") return true;
			found = hwnd;
			return false;
		}, IntPtr.Zero);
		if (found == IntPtr.Zero) throw new InvalidOperationException("No ZingWindow for process " + processId);
		SetForegroundWindow(found);
		Rect rect;
		var hr = DwmGetWindowAttribute(found, DWMWA_EXTENDED_FRAME_BOUNDS, out rect, Marshal.SizeOf(typeof(Rect)));
		if (hr != 0) throw new InvalidOperationException("DwmGetWindowAttribute failed: " + hr);
		return rect;
	}
}
'@

$rect = [ZingWindowShot]::Bounds($ProcessId)
Start-Sleep -Milliseconds 500
$width = $rect.Right - $rect.Left
$height = $rect.Bottom - $rect.Top
$bitmap = New-Object System.Drawing.Bitmap $width, $height
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, $bitmap.Size)
$graphics.Dispose()
New-Item -ItemType Directory -Force (Split-Path $Out) | Out-Null
$bitmap.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$bitmap.Dispose()
