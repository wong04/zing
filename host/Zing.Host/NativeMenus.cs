using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Zing.Core;

namespace Zing.Host;

/// <summary>Draws Zing.Core's menu model as WinUI flyout items.</summary>
public static class NativeMenus
{
	public static MenuFlyoutItemBase Build(Zing.Core.MenuItem item)
	{
		if (item.IsSeparator) return new MenuFlyoutSeparator();
		if (item.Submenu is { } children)
		{
			var sub = new MenuFlyoutSubItem { Text = item.Label, IsEnabled = item.Enabled };
			foreach (var child in children) sub.Items.Add(Build(child));
			return sub;
		}
		MenuFlyoutItem flyoutItem = item.Checked is { } isChecked
			? new ToggleMenuFlyoutItem { IsChecked = isChecked }
			: new MenuFlyoutItem();
		flyoutItem.Text = item.Label;
		flyoutItem.IsEnabled = item.Enabled;
		if (item.Accelerator is { } accelerator) flyoutItem.KeyboardAcceleratorTextOverride = accelerator;
		if (item.Click is { } click) flyoutItem.Click += (_, _) => click();
		return flyoutItem;
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
		_ = Windows.System.Launcher.LaunchUriAsync(new Uri(url));
	}
}
