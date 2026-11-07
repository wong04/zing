using Microsoft.Web.WebView2.Core;
using Zing.Core;

namespace Zing.Host;

/// <summary>
/// One window's downloads: the engine's download operations fed into Zing's downloads list (which
/// replaces Edge's download bubble), and the Mark of the Web on each finished file.
/// </summary>
public sealed class DownloadHost(Func<ChromeApi?> api)
{
	private readonly WindowsDownloads _downloads = new();

	/// <summary>Where downloads go; ZING_DOWNLOADS_DIR in tests. Read once, as the window is created.</summary>
	public string Directory { get; } = WindowsDownloads.Directory();

	/// <summary>File actions on downloads, and the engine's operations by id.</summary>
	public IDownloadHost Files => _downloads;

	public void Starting(WebView2Page page, CoreWebView2DownloadStartingEventArgs e)
	{
		if (api() is not { } chrome)
		{
			// No window API yet, so no list to show it in: cancelled, never left to Edge's own download bubble.
			e.Cancel = true;
			TestHooks.Record("download", new InvalidOperationException("A download started before the window's API"));
			return;
		}
		var operation = e.DownloadOperation;
		var name = DownloadNames.Suggested(operation.ContentDisposition, operation.Uri);
		var started = chrome.Downloads.Start(name, Math.Max(0L, (long)operation.TotalBytesToReceive));
		e.ResultFilePath = started.Path;
		// Zing's downloads list replaces Edge's download bubble.
		e.Handled = true;
		_downloads.Track(started.Id, operation);
		var lastPublish = DateTime.MinValue;
		operation.BytesReceivedChanged += (_, _) =>
		{
			chrome.Downloads.Progress(started.Id, operation.BytesReceived, Math.Max(0L, (long)operation.TotalBytesToReceive));
			if (DateTime.UtcNow - lastPublish < TimeSpan.FromMilliseconds(250)) return;
			lastPublish = DateTime.UtcNow;
			chrome.Downloads.Publish();
		};
		operation.StateChanged += (_, _) =>
		{
			var outcome = operation.State switch
			{
				CoreWebView2DownloadState.Completed => DownloadOutcome.Completed,
				CoreWebView2DownloadState.Interrupted when operation.InterruptReason == CoreWebView2DownloadInterruptReason.UserCanceled => DownloadOutcome.Cancelled,
				CoreWebView2DownloadState.Interrupted => DownloadOutcome.Interrupted,
				_ => (DownloadOutcome?)null,
			};
			if (outcome is not { } done) return;
			_downloads.Forget(started.Id);
			chrome.Downloads.Done(started.Id, done, operation.BytesReceived);
			if (done == DownloadOutcome.Completed) TestHooks.Background(MarkAsync(started, operation.Uri, page.Url), "mark of the web");
		};
	}

	/// <summary>
	/// Without this stream SmartScreen and Office never treat the file as downloaded. The engine
	/// stamps a generic about:internet mark; Zing's carries the real HostUrl and ReferrerUrl and is
	/// held against the engine's (holdMark.ts).
	/// </summary>
	private async Task MarkAsync(StartedDownload started, string hostUrl, string referrer)
	{
		try
		{
			// A download started from Zing's own start page has no web referrer.
			var ours = new ZoneMark(ZoneIdentifier.InternetZone, hostUrl, referrer.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? referrer : null);
			await MarkOfTheWeb.HoldAsync(new ZoneFile(started.Path), ours, windowMs: 10_000, pollMs: 100, ms => Task.Delay(ms));
		}
		catch (IOException error)
		{
			api()?.Downloads.MarkFailed(started.Id, error.Message);
		}
	}
}
