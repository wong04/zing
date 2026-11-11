namespace Zing.Core.Import;

/// <summary>Arc keeps passwords in iCloud Keychain (macOS) and has no export page to open.</summary>
public sealed class NoPasswordExportException() : InvalidOperationException("Arc has no password export to import from");

/// <summary>The browser isn't installed, or Windows has no record of where it is.</summary>
public sealed class BrowserNotInstalledException(ImportBrowser browser) : InvalidOperationException($"{browser} isn't installed on this PC");

/// <summary>Starts another browser at an address. Only the three browsers Zing imports passwords from.</summary>
public interface IBrowserLauncher
{
	/// <exception cref="BrowserNotInstalledException"/>
	void Open(ImportBrowser browser, string url);
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
		_ => throw new NoPasswordExportException(),
	};
}
