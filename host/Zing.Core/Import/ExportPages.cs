using System.Diagnostics;

namespace Zing.Core.Import;

/// <summary>Arc keeps passwords in iCloud Keychain (macOS); Dia is a packaged app whose export page isn't known. Neither has a page Zing can open.</summary>
public sealed class NoPasswordExportException(ImportBrowser browser) : InvalidOperationException($"{browser} has no password export page Zing can open");

/// <summary>The browser isn't installed, or Windows has no record of where it is.</summary>
public sealed class BrowserNotInstalledException(ImportBrowser browser) : InvalidOperationException($"{browser} isn't installed on this PC");

/// <summary>The source came from a damaged profile list, so there is no profile folder to open.</summary>
public sealed class NoProfileToOpenException(ImportBrowser browser) : InvalidOperationException($"Zing couldn't tell which {browser} profile to open");

/// <summary>Starts another browser in one of its profiles at an address. Only the three browsers Zing imports passwords from.</summary>
public interface IBrowserLauncher
{
	/// <param name="profile">The profile folder, "Default" or "Profile 1".</param>
	/// <exception cref="BrowserNotInstalledException"/>
	void Open(ImportBrowser browser, string profile, string url);
}

/// <summary>The command line that opens a browser's profile, as separate arguments: Windows quotes each one, so a profile name can't add an argument.</summary>
public static class BrowserLaunch
{
	/// <remarks>
	/// Without a profile, a browser with several shows its profile picker and then a plain new tab. Chromium also drops a chrome:// address
	/// given on its command line (startup_tab_provider.cc keeps web pages and the reset-settings page only), running or not, so the page itself may
	/// not open: the renderer shows the menu path to it as well.
	/// </remarks>
	public static IReadOnlyList<string> Arguments(string profile, string url) => [$"--profile-directory={profile}", url];

	public static ProcessStartInfo StartInfo(string exe, string profile, string url)
	{
		var info = new ProcessStartInfo { FileName = exe, UseShellExecute = false };
		foreach (var argument in Arguments(profile, url)) info.ArgumentList.Add(argument);
		return info;
	}
}

/// <summary>Sends a file to the Recycle Bin.</summary>
public interface IFileTrash
{
	void Recycle(string path);
}

public static class ExportPages
{
	/// <exception cref="NoPasswordExportException"/>
	public static string Url(ImportBrowser browser) => browser switch
	{
		ImportBrowser.Chrome => "chrome://password-manager/settings",
		ImportBrowser.Edge => "edge://wallet/passwords",
		ImportBrowser.Brave => "brave://password-manager/settings",
		_ => throw new NoPasswordExportException(browser),
	};

	/// <summary>Whether Zing can open the browser's export page, so its row offers Import passwords.</summary>
	public static bool Has(ImportBrowser browser) => browser is ImportBrowser.Chrome or ImportBrowser.Edge or ImportBrowser.Brave;
}
