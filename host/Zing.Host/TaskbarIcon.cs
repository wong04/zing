using Microsoft.UI.Windowing;
using Microsoft.Win32;
using Windows.UI.ViewManagement;

namespace Zing.Host;

/// <summary>
/// Keeps every window's taskbar button on the tile that contrasts with the
/// taskbar (taskbar-icon.ts): the frosted tile on a dark taskbar, the dark-rim
/// tile on a light one. The taskbar follows Windows mode, not Zing's own theme.
/// The .exe and shortcuts can't change at runtime, so they keep the frosted tile.
/// </summary>
public sealed class TaskbarIcon
{
	private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

	private readonly List<AppWindow> _windows = [];
	private readonly UISettings _ui = new();
	private string _icon;

	public TaskbarIcon(Microsoft.UI.Dispatching.DispatcherQueue dispatcher)
	{
		_icon = IconForTaskbar();
		// Raised off the UI thread when the user switches Windows between light and dark.
		_ui.ColorValuesChanged += (_, _) => dispatcher.TryEnqueue(Refresh);
	}

	public void Add(AppWindow window)
	{
		_windows.Add(window);
		window.SetIcon(_icon);
	}

	public void Remove(AppWindow window) => _windows.Remove(window);

	private void Refresh()
	{
		var icon = IconForTaskbar();
		if (icon == _icon) return;
		_icon = icon;
		foreach (var window in _windows) window.SetIcon(icon);
	}

	/// <summary>Windows before the light taskbar (pre-1903) have no value, and their taskbar is always dark.</summary>
	private static string IconForTaskbar()
	{
		using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
		var light = key?.GetValue("SystemUsesLightTheme") is int value && value != 0;
		var path = Path.Combine(AppContext.BaseDirectory, "Assets", light ? "icon-dark-tile.ico" : "icon.ico");
		if (!File.Exists(path)) throw new FileNotFoundException($"Taskbar icon missing: {path}", path);
		return path;
	}
}
