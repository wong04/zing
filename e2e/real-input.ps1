# Real OS input for the real-input tier (e2e/real-input.spec.ts), run by hand on a PC nobody is using.
# It brings Zing's window (the process -ProcessId) to the front, then sends a key chord (-Keys ctrl+t)
# or a click at a point of the window's content in DIPs (-X, -Y) through SendInput, as a person would.
# The click is the left button unless -Button middle, with -Hold shift (or ctrl) down around it.
# -AsIs sends to whatever is in front now, such as a page's own dialog, instead of bringing Zing's window there.
param(
	[Parameter(Mandatory = $true)][int]$ProcessId,
	[string]$Keys,
	[double]$X = -1,
	[double]$Y = -1,
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
	[DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, INPUT[] inputs, int size);

	private const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1, KEYEVENTF_KEYUP = 2, MOUSEEVENTF_LEFTDOWN = 2, MOUSEEVENTF_LEFTUP = 4, MOUSEEVENTF_MIDDLEDOWN = 0x20, MOUSEEVENTF_MIDDLEUP = 0x40;

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
		var scale = GetDpiForWindow(window) / 96.0;
		var origin = new POINT();
		ClientToScreen(window, ref origin);
		if (!SetCursorPos(origin.X + (int)Math.Round(x * scale), origin.Y + (int)Math.Round(y * scale))) throw new InvalidOperationException("SetCursorPos failed");
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
if (-not $AsIs) { [ZingRealInput]::Front($ProcessId, $window) }
if ($Keys) {
	[ZingRealInput]::Chord([uint16[]](Get-KeyCodes $Keys))
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
