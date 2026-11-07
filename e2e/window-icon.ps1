# Reads the icons Windows shows for Zing (e2e/brand.spec.ts): the big and small icons on the window
# of the process -ProcessId (what the taskbar and Alt+Tab draw) and the icon inside Zing.exe, and
# says which of the two shipped tiles each one is. Prints JSON. With -PreviewDir, also draws a
# 24 px taskbar button on a light and a dark Windows 11 taskbar: the live window's icon for the
# taskbar Windows is in now, and the tile Zing would set for the other.
param(
	[Parameter(Mandatory = $true)][int]$ProcessId,
	[Parameter(Mandatory = $true)][string]$Exe,
	[string]$PreviewDir
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class ZingWindowIcon
{
	private delegate bool EnumProc(IntPtr hwnd, IntPtr data);
	[DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc proc, IntPtr data);
	[DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
	[DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr hwnd, StringBuilder name, int size);
	[DllImport("user32.dll")] private static extern IntPtr SendMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
	[DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr LoadImageW(IntPtr instance, string name, uint type, int width, int height, uint flags);

	private const uint WM_GETICON = 0x7F, IMAGE_ICON = 1, LR_LOADFROMFILE = 0x10;

	/** One size from a .ico, decoded by Windows as it decodes icons it shows (System.Drawing skips PNG frames). */
	public static IntPtr FromFile(string path, int size)
	{
		var icon = LoadImageW(IntPtr.Zero, path, IMAGE_ICON, size, size, LR_LOADFROMFILE);
		if (icon == IntPtr.Zero) throw new InvalidOperationException("LoadImage failed for " + path + ": " + Marshal.GetLastWin32Error());
		return icon;
	}

	public static IntPtr Window(int processId)
	{
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
		return found;
	}

	/** 1 for the big icon, 0 for the small one. */
	public static IntPtr Icon(IntPtr hwnd, int which)
	{
		var icon = SendMessageW(hwnd, WM_GETICON, (IntPtr)which, IntPtr.Zero);
		if (icon == IntPtr.Zero) throw new InvalidOperationException("The window has no icon (WM_GETICON " + which + ")");
		return icon;
	}
}
'@

$assets = Join-Path (Split-Path $Exe) 'Assets'
$tiles = @{ frosted = (Join-Path $assets 'icon.ico'); dark = (Join-Path $assets 'icon-dark-tile.ico') }

function MeanDiff([System.Drawing.Bitmap]$a, [System.Drawing.Bitmap]$b) {
	if ($a.Width -ne $b.Width -or $a.Height -ne $b.Height) { throw "sizes differ: $($a.Width) and $($b.Width)" }
	$sum = 0
	for ($y = 0; $y -lt $a.Height; $y++) {
		for ($x = 0; $x -lt $a.Width; $x++) {
			$p = $a.GetPixel($x, $y); $q = $b.GetPixel($x, $y)
			$sum += [Math]::Abs($p.A - $q.A) + [Math]::Abs($p.R - $q.R) + [Math]::Abs($p.G - $q.G) + [Math]::Abs($p.B - $q.B)
		}
	}
	return [Math]::Round($sum / ($a.Width * $a.Height * 4), 2)
}

# How far a bitmap is from each shipped tile at the same size (mean channel difference, 0-255).
# Windows may resample an icon it is handed, so a match is near 0 rather than exactly 0.
function TileOf([System.Drawing.Bitmap]$bitmap) {
	$diff = @{}
	foreach ($name in $tiles.Keys) {
		$reference = [System.Drawing.Icon]::FromHandle([ZingWindowIcon]::FromFile($tiles[$name], $bitmap.Width)).ToBitmap()
		$diff[$name] = MeanDiff $bitmap $reference
	}
	return @{ size = $bitmap.Width; diff = $diff }
}

$hwnd = [ZingWindowIcon]::Window($ProcessId)
$big = [System.Drawing.Icon]::FromHandle([ZingWindowIcon]::Icon($hwnd, 1)).ToBitmap()
$small = [System.Drawing.Icon]::FromHandle([ZingWindowIcon]::Icon($hwnd, 0)).ToBitmap()
$exeIcon = [System.Drawing.Icon]::ExtractAssociatedIcon($Exe).ToBitmap()
$personalize = Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize' -ErrorAction SilentlyContinue
$light = $null -ne $personalize -and $personalize.SystemUsesLightTheme -eq 1

if ($PreviewDir) {
	New-Item -ItemType Directory -Force $PreviewDir | Out-Null
	$live = New-Object System.Drawing.Icon([System.Drawing.Icon]::FromHandle([ZingWindowIcon]::Icon($hwnd, 1)), 24, 24)
	foreach ($mode in 'light', 'dark') {
		$isLive = ($mode -eq 'light') -eq $light
		$icon = if ($isLive) { $live.ToBitmap() } else { [System.Drawing.Icon]::FromHandle([ZingWindowIcon]::FromFile($tiles[$(if ($mode -eq 'light') { 'dark' } else { 'frosted' })], 24)).ToBitmap() }
		$canvas = New-Object System.Drawing.Bitmap 360, 48
		$g = [System.Drawing.Graphics]::FromImage($canvas)
		$g.SmoothingMode = 'AntiAlias'
		$bar = if ($mode -eq 'light') { [System.Drawing.Color]::FromArgb(238, 238, 238) } else { [System.Drawing.Color]::FromArgb(28, 28, 28) }
		$slot = if ($mode -eq 'light') { [System.Drawing.Color]::FromArgb(200, 200, 204) } else { [System.Drawing.Color]::FromArgb(70, 70, 76) }
		$g.Clear($bar)
		foreach ($x in 120, 160, 240) { $g.FillRectangle((New-Object System.Drawing.SolidBrush($slot)), $x + 4, 12, 24, 24) }
		$g.FillRectangle((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(40, 128, 128, 128))), 196, 4, 40, 40)
		$g.DrawImage($icon, 204, 12, 24, 24)
		$g.FillRectangle((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(0, 120, 212))), 210, 42, 12, 3)
		$text = if ($mode -eq 'light') { [System.Drawing.Color]::FromArgb(30, 30, 30) } else { [System.Drawing.Color]::FromArgb(230, 230, 230) }
		$label = if ($isLive) { "$mode taskbar (live)" } else { "$mode taskbar" }
		$g.DrawString($label, (New-Object System.Drawing.Font('Segoe UI', 9)), (New-Object System.Drawing.SolidBrush($text)), 8, 15)
		$scaled = New-Object System.Drawing.Bitmap 720, 96
		$s = [System.Drawing.Graphics]::FromImage($scaled)
		$s.InterpolationMode = 'NearestNeighbor'
		$s.PixelOffsetMode = 'Half'
		$s.DrawImage($canvas, 0, 0, 720, 96)
		$scaled.Save((Join-Path $PreviewDir "taskbar-$mode.png"), [System.Drawing.Imaging.ImageFormat]::Png)
	}
}

@{
	taskbar = $(if ($light) { 'light' } else { 'dark' })
	big = TileOf $big
	small = TileOf $small
	exe = TileOf $exeIcon
} | ConvertTo-Json -Compress -Depth 4
