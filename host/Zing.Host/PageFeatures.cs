using System.Diagnostics;
using Microsoft.Web.WebView2.Core;
using Zing.Core;

namespace Zing.Host;

/// <summary>A page's downloads and permission requests go to its window's lists (downloads.ts, permissions.ts).</summary>
public interface IPageFeatureHost
{
	void DownloadStarting(WebView2Page page, CoreWebView2DownloadStartingEventArgs e);
	/// <summary>Where downloads go; also the engine's own default, so it never looks in the user's real Downloads folder for names.</summary>
	string DownloadsDirectory { get; }
	void PermissionRequested(WebView2Page page, CoreWebView2PermissionRequestedEventArgs e);
	/// <summary>Whether the page's site was allowed pop-ups this session, so one opened without a click opens.</summary>
	bool PopupsAllowed(WebView2Page page);
	/// <summary>The page opened a pop-up to <paramref name="url"/> without a click, and the engine was told no: the person is offered it.</summary>
	void PopupBlocked(WebView2Page page, string url);
	/// <summary>The page went to a new document or closed: its session-only grants, queued prompts and dialogs go.</summary>
	void Left(WebView2Page page);
	/// <summary>The page's renderer died: its dialogs went with it, and the window's prompts for them go too.</summary>
	void DialogsGone(WebView2Page page);
	/// <summary>A frame of the page opened a JavaScript dialog (a <see cref="ScriptDialogKinds"/> kind), which waits for <paramref name="answer"/>.</summary>
	void ScriptDialog(WebView2Page page, string kind, string frameUrl, string message, string defaultText, IScriptDialogAnswer answer);
	/// <summary>An element of the page entered or left fullscreen (a video's fullscreen button, requestFullscreen).</summary>
	void FullScreenChanged(WebView2Page page);
	/// <summary>The password vault is on, so pages get the autofill script and may post its messages.</summary>
	bool AutofillOn { get; }
	/// <summary>The blockTrackers setting: tracker domains are refused as third-party requests and Edge's tracking prevention is Balanced.</summary>
	bool BlockTrackers { get; }
	/// <summary>The page is a layer of the window's link preview.</summary>
	bool InPreview(WebView2Page page);
	/// <summary>The page's view now exists, placed and shown as asked: a raise asked for before then did nothing.</summary>
	void ViewStarted(WebView2Page page);
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

	/// <summary>The root browser profiles live under; ZING_LOCALAPPDATA in tests, so they never read the real profiles.</summary>
	public static string LocalAppData()
	{
		var configured = Environment.GetEnvironmentVariable("ZING_LOCALAPPDATA");
		// Test seam: a big Chrome or Edge profile makes the New Tab import scan slow; the delay stands in for it.
		if (TestHooks.IsEndToEndRun && int.TryParse(Environment.GetEnvironmentVariable("ZING_LOCALAPPDATA_DELAY_MS"), out var delay)) Thread.Sleep(delay);
		if (!string.IsNullOrEmpty(configured)) return configured;
		if (TestHooks.IsEndToEndRun) throw new InvalidOperationException("e2e runs must set ZING_LOCALAPPDATA so they never read the real browser profiles");
		return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
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
		// A grant lasts as long as the tab (PermissionBroker): the engine must not keep it, or a refusal, in the profile.
		request.SavesInProfile = false;
		request.State = granted ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny;
		deferral.Complete();
	}

	/// <summary>WebView2's kinds in Electron's names, which the broker and its prompts use; the broker refuses anything it doesn't name.</summary>
	public static (string Permission, PermissionDetails Details) Describe(CoreWebView2PermissionRequestedEventArgs e) => e.PermissionKind switch
	{
		CoreWebView2PermissionKind.Camera => ("media", new PermissionDetails(e.Uri, ["video"])),
		CoreWebView2PermissionKind.Microphone => ("media", new PermissionDetails(e.Uri, ["audio"])),
		CoreWebView2PermissionKind.Geolocation => ("geolocation", new PermissionDetails(e.Uri)),
		CoreWebView2PermissionKind.Notifications => ("notifications", new PermissionDetails(e.Uri)),
		CoreWebView2PermissionKind.ClipboardRead => ("clipboard-read", new PermissionDetails(e.Uri)),
		CoreWebView2PermissionKind.MultipleAutomaticDownloads => ("multiple-automatic-downloads", new PermissionDetails(e.Uri)),
		CoreWebView2PermissionKind.FileReadWrite => ("file-read-write", new PermissionDetails(e.Uri)),
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
/// It also clears the whole profile for Settings' Clear browsing session, persistent or not.
/// </summary>
public sealed class WebViewSitePurger(Func<CoreWebView2?> anyLivePage, Func<bool> anyWindowOpen) : ISitePurger, IBrowsingData
{
	private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(50);

	public async Task PurgeAsync(SiteToPurge site)
	{
		if (await LivePageAsync() is not { } core) return;
		foreach (var origin in site.Origins)
		{
			var request = System.Text.Json.JsonSerializer.Serialize(new { origin, storageTypes = "all" });
			await core.CallDevToolsProtocolMethodAsync("Storage.clearDataForOrigin", request);
		}
		foreach (var cookie in await core.CookieManager.GetCookiesAsync(""))
			if (RegistrableDomain.Of(cookie.Domain.TrimStart('.')) == site.Domain) core.CookieManager.DeleteCookie(cookie);
	}

	/// <summary>Every data kind the pages' profile holds: cookies, storage, cache, HTTP auth, and the engine's own history and autofill.</summary>
	public async Task ClearAllAsync()
	{
		if (await LivePageAsync() is not { } core) return;
		await core.Profile.ClearBrowsingDataAsync();
	}

	private async Task<CoreWebView2?> LivePageAsync()
	{
		var core = anyLivePage();
		while (core is null && anyWindowOpen())
		{
			await Task.Delay(Poll);
			core = anyLivePage();
		}
		return core;
	}
}
