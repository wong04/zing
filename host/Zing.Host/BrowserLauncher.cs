using System.Diagnostics;
using Microsoft.Win32;
using Zing.Core.Import;

namespace Zing.Host;

/// <summary>
/// Starts the browser's own exe with the page as its one argument, never through the shell, so the address can't be taken for anything
/// else. E2e runs start nothing: the launch goes to ZING_DIALOGS_FILE.
/// </summary>
public sealed class BrowserLauncher : IBrowserLauncher
{
	private const string AppPaths = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\";

	public void Open(ImportBrowser browser, string url)
	{
		if (TestHooks.IsEndToEndRun)
		{
			TestHooks.RecordDialog("launch:" + browser.ToString().ToLowerInvariant(), url);
			return;
		}
		var exe = FindExe(browser) ?? throw new BrowserNotInstalledException(browser);
		using var process = Process.Start(new ProcessStartInfo { FileName = exe, UseShellExecute = false, ArgumentList = { url } });
	}

	private static string? FindExe(ImportBrowser browser)
	{
		var name = browser switch
		{
			ImportBrowser.Chrome => "chrome.exe",
			ImportBrowser.Edge => "msedge.exe",
			ImportBrowser.Brave => "brave.exe",
			_ => throw new NoPasswordExportException(),
		};
		foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
		{
			using var key = hive.OpenSubKey(AppPaths + name);
			if (key?.GetValue(null) is string { Length: > 0 } path && File.Exists(path)) return path;
		}
		return null;
	}
}
