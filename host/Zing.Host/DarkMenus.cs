using System.Runtime.InteropServices;
using Microsoft.Win32;
using Windows.UI.ViewManagement;
using Zing.Core;

namespace Zing.Host;

/// <summary>
/// Makes Zing's Win32 popup menus (NativeMenus) draw in Zing's effective theme. Win32 menus draw light unless the
/// process opts in through uxtheme's undocumented dark-mode exports, the ones File Explorer and Notepad++ use.
/// Only the process's own preferred mode changes, never the system's. Submenus and grayed items follow it.
/// </summary>
public sealed class DarkMenus
{
	private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

	[UnmanagedFunctionPointer(CallingConvention.Winapi)]
	private delegate int SetPreferredAppModeFn(int mode);

	[UnmanagedFunctionPointer(CallingConvention.Winapi)]
	[return: MarshalAs(UnmanagedType.U1)]
	private delegate bool AllowDarkModeForWindowFn(nint hwnd, [MarshalAs(UnmanagedType.U1)] bool allow);

	[UnmanagedFunctionPointer(CallingConvention.Winapi)]
	private delegate void FlushMenuThemesFn();

	private readonly SetPreferredAppModeFn _setPreferredAppMode;
	private readonly AllowDarkModeForWindowFn _allowDarkModeForWindow;
	private readonly FlushMenuThemesFn _flushMenuThemes;
	private readonly Func<string> _theme;
	private readonly UISettings _ui = new();

	/// <summary>The mode last applied, for the e2e "menu-theme" hook; null before the first.</summary>
	public static PreferredAppMode? Applied { get; private set; }

	/// <summary>Throws UxThemeExportMissingException when this Windows lacks one of the exports.</summary>
	public DarkMenus(Microsoft.UI.Dispatching.DispatcherQueue dispatcher, Func<string> theme)
	{
		var uxtheme = NativeLibrary.Load("uxtheme.dll");
		var exports = UxThemeExports.Resolve(ordinal => GetProcAddress(uxtheme, ordinal));
		_setPreferredAppMode = Marshal.GetDelegateForFunctionPointer<SetPreferredAppModeFn>(exports.SetPreferredAppMode);
		_allowDarkModeForWindow = Marshal.GetDelegateForFunctionPointer<AllowDarkModeForWindowFn>(exports.AllowDarkModeForWindow);
		_flushMenuThemes = Marshal.GetDelegateForFunctionPointer<FlushMenuThemesFn>(exports.FlushMenuThemes);
		_theme = theme;
		// Raised off the UI thread when the user switches Windows between light and dark; "system" follows it.
		_ui.ColorValuesChanged += (_, _) => Faults.Enqueue(dispatcher, Follow, "the menu theme refresh");
		Follow();
	}

	/// <summary>Lets the menus a window owns go dark; called once per window, before it shows a menu.</summary>
	public void Allow(nint hwnd) => _allowDarkModeForWindow(hwnd, true);

	/// <summary>Applies the effective theme now; the next menu shows it.</summary>
	public void Follow()
	{
		var mode = MenuTheme.Mode(_theme(), SystemDark());
		_setPreferredAppMode((int)mode);
		_flushMenuThemes();
		Applied = mode;
	}

	/// <summary>Windows' app mode, which "system" means. Before 1903 there is no value and apps are light.</summary>
	private static bool SystemDark()
	{
		using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
		return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
	}

	// GetProcAddress takes an ordinal in place of a name (MAKEINTRESOURCE); NativeLibrary.TryGetExport takes names only.
	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern nint GetProcAddress(nint module, nint ordinal);
}
