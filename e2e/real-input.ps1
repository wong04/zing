# Real OS input for the real-input tier (e2e/real-input.spec.ts), run by hand on a PC nobody is using.
# It brings Zing's window (the process -ProcessId) to the front, then sends a key chord (-Keys ctrl+t)
# or a click at a point of the window's content in DIPs (-X, -Y) through SendInput, as a person would.
# The click is the left button unless -Button middle, with -Hold shift (or ctrl) down around it.
# -AsIs sends to whatever is in front now, such as a page's own dialog, instead of bringing Zing's window there.
# With -ToX and -ToY as well, the left button is pressed at (-X, -Y), moved to (-ToX, -ToY) and released there: a drag.
# -Where prints the window's top-left on the screen, in physical pixels, as "x y", and sends nothing.
param(
	[Parameter(Mandatory = $true)][int]$ProcessId,
	[string]$Keys,
	[double]$X = -1,
	[double]$Y = -1,
	[double]$ToX = -1,
	[double]$ToY = -1,
	[switch]$Where,
	[ValidateSet('left', 'middle')][string]$Button = 'left',
	[string]$Hold,
	[switch]$AsIs
)
$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

public static class ZingRealInput
{
	private delegate bool EnumProc(IntPtr hwnd, IntPtr data);

	[StructLayout(LayoutKind.Sequential)] private struct POINT { public int X; public int Y; }
	[StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
	[StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int Dx; public int Dy; public uint MouseData; public uint Flags; public uint Time; public IntPtr ExtraInfo; }
	[StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT { public ushort Vk; public ushort Scan; public uint Flags; public uint Time; public IntPtr ExtraInfo; }
	[StructLayout(LayoutKind.Explicit)] private struct INPUTUNION { [FieldOffset(0)] public MOUSEINPUT Mouse; [FieldOffset(0)] public KEYBDINPUT Key; }
	[StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint Type; public INPUTUNION U; }

	[DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc proc, IntPtr data);
	[DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
	[DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr hwnd, StringBuilder name, int size);
	[DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
	[DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
	[DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
	[DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
	[DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hwnd, ref POINT point);
	[DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
	[DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
	[DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
	[DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
	[DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);

	private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new IntPtr(-4);
	private const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1, KEYEVENTF_KEYUP = 2, MOUSEEVENTF_MOVE = 1, MOUSEEVENTF_LEFTDOWN = 2, MOUSEEVENTF_LEFTUP = 4, MOUSEEVENTF_MIDDLEDOWN = 0x20, MOUSEEVENTF_MIDDLEUP = 0x40, MOUSEEVENTF_VIRTUALDESK = 0x4000, MOUSEEVENTF_ABSOLUTE = 0x8000;
	private const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;

	/// <summary>Zing's main window (class ZingWindow) of this process.</summary>
	public static IntPtr MainWindow(int processId)
	{
		var found = IntPtr.Zero;
		EnumWindows((hwnd, _) =>
		{
			uint pid;
			GetWindowThreadProcessId(hwnd, out pid);
			if (pid != processId || !IsWindowVisible(hwnd)) return true;
			var name = new StringBuilder(64);
			GetClassNameW(hwnd, name, name.Capacity);
			if (name.ToString() != "ZingWindow") return true;
			found = hwnd;
			return false;
		}, IntPtr.Zero);
		if (found == IntPtr.Zero) throw new InvalidOperationException("No visible Zing window for process " + processId);
		return found;
	}

	/// <summary>
	/// Zing in front: its window or one of its overlays. Windows only lets the process that sent the last
	/// input take the foreground, so a lone Alt tap comes first when it is not.
	/// </summary>
	public static void Front(int processId, IntPtr window)
	{
		for (var attempt = 0; attempt < 20; attempt++)
		{
			uint pid;
			GetWindowThreadProcessId(GetForegroundWindow(), out pid);
			if (pid == processId) return;
			Send(new[] { Key(0x12, false), Key(0x12, true) });
			SetForegroundWindow(window);
			Thread.Sleep(100);
		}
		throw new InvalidOperationException("Zing would not come to the front");
	}

	public static void Chord(ushort[] keys)
	{
		var inputs = new INPUT[keys.Length * 2];
		for (var i = 0; i < keys.Length; i++)
		{
			inputs[i] = Key(keys[i], false);
			inputs[keys.Length * 2 - 1 - i] = Key(keys[i], true);
		}
		Send(inputs);
	}

	/// <summary>A click at a point of the window's content, in DIPs, with the held keys down around it.</summary>
	public static void Click(IntPtr window, double x, double y, bool middle, ushort[] held)
	{
		var at = ToScreen(window, x, y);
		if (!SetCursorPos(at.X, at.Y)) throw new InvalidOperationException("SetCursorPos failed");
		Thread.Sleep(50);
		foreach (var key in held) Send(new[] { Key(key, false) });
		try
		{
			Send(middle ? new[] { Mouse(MOUSEEVENTF_MIDDLEDOWN), Mouse(MOUSEEVENTF_MIDDLEUP) } : new[] { Mouse(MOUSEEVENTF_LEFTDOWN), Mouse(MOUSEEVENTF_LEFTUP) });
			// The page asks for its window on the release; the keys stay down until it has.
			Thread.Sleep(300);
		}
		finally
		{
			foreach (var key in held) Send(new[] { Key(key, true) });
		}
	}

	/// <summary>
	/// The left button pressed at one point of the window's content, moved in steps to another and released there, in DIPs.
	/// Both points are taken before the press, so a window the drag moves doesn't move the target with it.
	/// </summary>
	public static void Drag(IntPtr window, double x, double y, double toX, double toY)
	{
		var from = ToScreen(window, x, y);
		var to = ToScreen(window, toX, toY);
		if (!SetCursorPos(from.X, from.Y)) throw new InvalidOperationException("SetCursorPos failed");
		Thread.Sleep(50);
		Send(new[] { Mouse(MOUSEEVENTF_LEFTDOWN) });
		try
		{
			Thread.Sleep(100);
			const int steps = 20;
			for (var i = 1; i <= steps; i++)
			{
				Send(new[] { MoveTo(from.X + (to.X - from.X) * i / steps, from.Y + (to.Y - from.Y) * i / steps) });
				Thread.Sleep(15);
			}
			Thread.Sleep(100);
		}
		finally
		{
			Send(new[] { Mouse(MOUSEEVENTF_LEFTUP) });
		}
		Thread.Sleep(300);
	}

	/// <summary>The window's top-left on the screen, in physical pixels.</summary>
	public static string Where(IntPtr window)
	{
		AwareOfDpi();
		RECT rect;
		if (!GetWindowRect(window, out rect)) throw new InvalidOperationException("GetWindowRect failed");
		return rect.Left + " " + rect.Top;
	}

	// Windows PowerShell is DPI-unaware, so it sees screen points scaled to 96 DPI while GetDpiForWindow reports
	// Zing's real DPI: per-monitor aware, every point here is a physical pixel.
	private static void AwareOfDpi()
	{
		if (SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2) == IntPtr.Zero) throw new InvalidOperationException("SetThreadDpiAwarenessContext failed");
	}

	private static POINT ToScreen(IntPtr window, double x, double y)
	{
		AwareOfDpi();
		var scale = GetDpiForWindow(window) / 96.0;
		var origin = new POINT();
		ClientToScreen(window, ref origin);
		return new POINT { X = origin.X + (int)Math.Round(x * scale), Y = origin.Y + (int)Math.Round(y * scale) };
	}

	/// <summary>A mouse move to a screen point, as SendInput wants it: 0–65535 across the whole virtual desktop.</summary>
	private static INPUT MoveTo(int x, int y)
	{
		var input = Mouse(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK);
		input.U.Mouse.Dx = (int)Math.Round((x - GetSystemMetrics(SM_XVIRTUALSCREEN)) * 65535.0 / (GetSystemMetrics(SM_CXVIRTUALSCREEN) - 1));
		input.U.Mouse.Dy = (int)Math.Round((y - GetSystemMetrics(SM_YVIRTUALSCREEN)) * 65535.0 / (GetSystemMetrics(SM_CYVIRTUALSCREEN) - 1));
		return input;
	}

	private static INPUT Key(ushort vk, bool up)
	{
		var input = new INPUT { Type = INPUT_KEYBOARD };
		input.U.Key.Vk = vk;
		input.U.Key.Flags = up ? KEYEVENTF_KEYUP : 0;
		return input;
	}

	private static INPUT Mouse(uint flags)
	{
		var input = new INPUT { Type = INPUT_MOUSE };
		input.U.Mouse.Flags = flags;
		return input;
	}

	private static void Send(INPUT[] inputs)
	{
		var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT)));
		if (sent != inputs.Length) throw new InvalidOperationException("SendInput sent " + sent + " of " + inputs.Length + " (error " + Marshal.GetLastWin32Error() + ")");
	}
}
'@

function Get-KeyCodes([string]$chord) {
	foreach ($part in $chord.ToLowerInvariant().Split('+')) {
		switch ($part) {
			'ctrl' { 0x11 }
			'shift' { 0x10 }
			'alt' { 0x12 }
			'esc' { 0x1B }
			default {
				if ($part.Length -ne 1 -or $part -notmatch '^[a-z0-9]$') { throw "Unknown key '$part' in '$chord'" }
				[int][char]$part.ToUpperInvariant()
			}
		}
	}
}

$window = [ZingRealInput]::MainWindow($ProcessId)
if ($Where) {
	[ZingRealInput]::Where($window)
	return
}
if (-not $AsIs) { [ZingRealInput]::Front($ProcessId, $window) }
if ($Keys) {
	[ZingRealInput]::Chord([uint16[]](Get-KeyCodes $Keys))
}
elseif ($X -ge 0 -and $Y -ge 0 -and $ToX -ge 0 -and $ToY -ge 0) {
	[ZingRealInput]::Drag($window, $X, $Y, $ToX, $ToY)
}
elseif ($X -ge 0 -and $Y -ge 0) {
	# Typed first: an empty array returned from an if would arrive as $null.
	[uint16[]]$held = @()
	if ($Hold) { $held = Get-KeyCodes $Hold }
	[ZingRealInput]::Click($window, $X, $Y, $Button -eq 'middle', $held)
}
else {
	throw 'Pass -Keys or -X and -Y'
}
