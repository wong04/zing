using System.Diagnostics;
using Microsoft.Web.WebView2.Core;
using Zing.Core;

namespace Zing.Host;

/// <summary>A page's downloads and permission requests go to its window's lists (downloads.ts, permissions.ts).</summary>
public interface IPageFeatureHost
{
	void DownloadStarting(WebView2Page page, CoreWebView2DownloadStartingEventArgs e);
	void PermissionRequested(WebView2Page page, CoreWebView2PermissionRequestedEventArgs e);
	/// <summary>The page went to a new document or closed: its session-only grants and queued prompts go.</summary>
	void Left(WebView2Page page);
}

/// <summary>
/// File actions on downloads, and the engine's download operations by id.
/// Test runs never open Explorer or another app (AGENTS.md).
/// </summary>
public sealed class WindowsDownloads : IDownloadHost
{
	private readonly Dictionary<int, CoreWebView2DownloadOperation> _operations = [];

	/// <summary>The user's Downloads folder; ZING_DOWNLOADS_DIR in tests.</summary>
	public static string Directory()
	{
		var configured = Environment.GetEnvironmentVariable("ZING_DOWNLOADS_DIR");
		if (!string.IsNullOrEmpty(configured)) return configured;
		if (TestHooks.IsEndToEndRun) throw new InvalidOperationException("e2e runs must set ZING_DOWNLOADS_DIR so they never write to the real Downloads folder");
		return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
	}

	public void Track(int id, CoreWebView2DownloadOperation operation) => _operations[id] = operation;

	public void Forget(int id) => _operations.Remove(id);

	public bool FileExists(string path) => File.Exists(path);

	public string? OpenFile(string path)
	{
		if (TestHooks.IsEndToEndRun) throw new InvalidOperationException($"e2e runs must not open {path} in another app");
		try
		{
			Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
			return null;
		}
		catch (System.ComponentModel.Win32Exception error)
		{
			return error.Message;
		}
	}

	public void ShowInFolder(string path)
	{
		if (TestHooks.IsEndToEndRun) throw new InvalidOperationException($"e2e runs must not open Explorer at {path}");
		Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
	}

	public void Cancel(int id)
	{
		if (_operations.TryGetValue(id, out var operation)) operation.Cancel();
	}
}

/// <summary>Answers a WebView2 permission request once the person (or a remembered grant) decides.</summary>
public sealed class PermissionDeferral(CoreWebView2PermissionRequestedEventArgs request, Windows.Foundation.Deferral deferral) : IPermissionResolver
{
	public void Resolve(bool granted)
	{
		request.State = granted ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny;
		deferral.Complete();
	}

	/// <summary>WebView2's kinds in Electron's names, which the broker and its prompts use; anything else is refused.</summary>
	public static (string Permission, PermissionDetails Details) Describe(CoreWebView2PermissionRequestedEventArgs e) => e.PermissionKind switch
	{
		CoreWebView2PermissionKind.Camera => ("media", new PermissionDetails(e.Uri, ["video"])),
		CoreWebView2PermissionKind.Microphone => ("media", new PermissionDetails(e.Uri, ["audio"])),
		CoreWebView2PermissionKind.Geolocation => ("geolocation", new PermissionDetails(e.Uri)),
		CoreWebView2PermissionKind.Notifications => ("notifications", new PermissionDetails(e.Uri)),
		CoreWebView2PermissionKind.ClipboardRead => ("clipboard-read", new PermissionDetails(e.Uri)),
		var other => (other.ToString(), new PermissionDetails(e.Uri)),
	};
}

/// <summary>A file's Zone.Identifier alternate data stream, which .NET addresses as "file:Zone.Identifier".</summary>
public sealed class ZoneFile(string path) : IMarkStore
{
	private string Stream => path + ":Zone.Identifier";

	public ZoneMark? Read() => File.Exists(Stream) ? ZoneIdentifier.Parse(File.ReadAllText(Stream)) : null;

	public void Write(ZoneMark mark) => File.WriteAllText(Stream, ZoneIdentifier.Format(mark));
}

/// <summary>
/// Clears a site from the in-memory profile through any live page of the
/// profile (W0 proved this): DevTools' Storage.clearDataForOrigin per origin,
/// then every cookie of the registrable domain, which a per-origin clear misses.
/// Closing a window's last tab leaves no live page for a moment, until the fresh tab starts,
/// so this waits for one. If none comes, every window has closed: Zing is quitting, and the
/// in-memory profile, with all its data, goes with the process.
/// </summary>
public sealed class WebViewSitePurger(Func<CoreWebView2?> anyLivePage, Func<bool> anyWindowOpen) : ISitePurger
{
	private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(50);

	public async Task PurgeAsync(SiteToPurge site)
	{
		var core = anyLivePage();
		while (core is null && anyWindowOpen())
		{
			await Task.Delay(Poll);
			core = anyLivePage();
		}
		if (core is null) return;
		foreach (var origin in site.Origins)
		{
			var request = System.Text.Json.JsonSerializer.Serialize(new { origin, storageTypes = "all" });
			await core.CallDevToolsProtocolMethodAsync("Storage.clearDataForOrigin", request);
		}
		foreach (var cookie in await core.CookieManager.GetCookiesAsync(""))
			if (RegistrableDomain.Of(cookie.Domain.TrimStart('.')) == site.Domain) core.CookieManager.DeleteCookie(cookie);
	}
}
