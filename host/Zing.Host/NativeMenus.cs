using Windows.ApplicationModel.DataTransfer;
using Zing.Core;

namespace Zing.Host;

/// <summary>
/// Shows Zing.Core's menu model as a Win32 popup menu: a window of its own, above the window's views.
/// A XAML MenuFlyout would draw inside WinUI's content, which Zing's window no longer has.
/// </summary>
public static class NativeMenus
{
	private const uint MF_STRING = 0x0, MF_GRAYED = 0x1, MF_CHECKED = 0x8, MF_POPUP = 0x10, MF_SEPARATOR = 0x800;

	/// <summary>Shows the menu at a point of the window's content, in DIPs, and runs the chosen item.</summary>
	public static void Show(HostWindow window, IReadOnlyList<Zing.Core.MenuItem> items, double x, double y)
	{
		var scale = window.Scale;
		var origin = window.ClientOrigin;
		var clicks = new List<Action>();
		var menu = Build(items, clicks);
		try
		{
			const uint TPM_RETURNCMD = 0x100, TPM_RIGHTBUTTON = 0x2;
			// The menu only closes on a click elsewhere when its owner is the foreground window.
			Native.SetForegroundWindow(window.Hwnd);
			var chosen = Native.TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, origin.X + (int)Math.Round(x * scale), origin.Y + (int)Math.Round(y * scale), window.Hwnd, 0);
			if (chosen > 0) TestHooks.Guard(clicks[chosen - 1], "menu click");
		}
		finally
		{
			Native.DestroyMenu(menu);
		}
	}

	private static nint Build(IReadOnlyList<Zing.Core.MenuItem> items, List<Action> clicks)
	{
		var menu = Native.CreatePopupMenu();
		if (menu == 0) throw new NativeCallException("CreatePopupMenu");
		foreach (var item in items)
		{
			if (item.IsSeparator)
			{
				Append(menu, MF_SEPARATOR, 0, null);
				continue;
			}
			var flags = item.Enabled ? MF_STRING : MF_GRAYED;
			if (item.Submenu is { } children)
			{
				Append(menu, flags | MF_POPUP, Build(children, clicks), Text(item));
				continue;
			}
			if (item.Checked == true) flags |= MF_CHECKED;
			// Ids start at 1: TrackPopupMenuEx returns 0 when nothing was chosen.
			clicks.Add(item.Click ?? (() => { }));
			Append(menu, flags, clicks.Count, Text(item));
		}
		return menu;
	}

	/// <summary>A tab separates the label from the right-aligned shortcut; &amp; would underline the next letter.</summary>
	private static string Text(Zing.Core.MenuItem item)
	{
		var label = item.Label!.Replace("&", "&&");
		return item.Accelerator is { } accelerator ? $"{label}\t{accelerator}" : label;
	}

	private static void Append(nint menu, uint flags, nint id, string? text)
	{
		if (!Native.AppendMenuW(menu, flags, id, text)) throw new NativeCallException("AppendMenu");
	}
}

/// <summary>The clipboard and the default browser. Test runs never open another app (AGENTS.md).</summary>
public sealed class WindowsShell : IShellActions
{
	public void CopyText(string text)
	{
		var package = new DataPackage();
		package.SetText(text);
		Clipboard.SetContent(package);
	}

	public void OpenExternal(string url)
	{
		if (TestHooks.IsEndToEndRun) throw new InvalidOperationException($"e2e runs must not open {url} in another app");
		// Windows' own pages (ms-settings:) as well as web links.
		TestHooks.Background(LaunchAsync(new Uri(url)), "open external");
	}

	/// <summary>Windows answers false when no app takes the link; the address stays out of the error, which may reach the fault log.</summary>
	private static async Task LaunchAsync(Uri uri)
	{
		if (!await Windows.System.Launcher.LaunchUriAsync(uri)) throw new InvalidOperationException($"No app opened the {uri.Scheme}: link");
	}
}
