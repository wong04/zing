using Microsoft.Web.WebView2.Core;
using Zing.Core;
using Zing.Core.Engines;

namespace Zing.Host;

/// <summary>
/// Prints an HTML page a person picked to a PDF, the way Convert does. The page runs in a browser of its own, apart
/// from the one that shows websites: its own profile folder (deleted afterwards), no scripts, and no network. The page
/// is given as text (NavigateToString), so it has no file address and can't read the files beside it.
/// </summary>
internal static class HtmlPdfPrinter
{
	/// <summary>No name can be looked up and every connection goes to a closed local port, so even a preconnect or a prefetch (which WebResourceRequested does not see) has nowhere to go.</summary>
	private const string NoNetworkArguments = "--host-resolver-rules=\"MAP * ~NOTFOUND\" --proxy-server=127.0.0.1:9";

	private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

	/// <summary>One print at a time: each starts a browser of its own, and a compromised renderer must not be able to start many.</summary>
	private static readonly SemaphoreSlim Turn = new(1, 1);

	public static async Task PrintAsync(HostWindow window, WindowScope scope, string html, string pdfPath, CancellationToken cancellation)
	{
		await Turn.WaitAsync(cancellation);
		try
		{
			await PrintOneAsync(window, scope, html, pdfPath, cancellation);
		}
		finally
		{
			Turn.Release();
		}
	}

	private static async Task PrintOneAsync(HostWindow window, WindowScope scope, string html, string pdfPath, CancellationToken cancellation)
	{
		var profile = Path.Combine(AppPaths.PrintScratch, Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(profile);
		HostedView? view = null;
		try
		{
			var options = new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = NoNetworkArguments };
			var environment = await CoreWebView2Environment.CreateWithOptionsAsync(null, profile, options);
			var controllerOptions = environment.CreateCoreWebView2ControllerOptions();
			controllerOptions.IsInPrivateModeEnabled = true;
			view = await HostedView.CreateAsync(window, scope, environment, controllerOptions, "print", overlay: false);
			view.Visible = false;
			var core = view.Core;
			core.Settings.IsScriptEnabled = false;
			core.Settings.AreHostObjectsAllowed = false;
			core.Settings.IsWebMessageEnabled = false;
			core.Settings.AreDefaultScriptDialogsEnabled = false;
			core.Settings.AreDefaultContextMenusEnabled = false;
			core.Settings.AreDevToolsEnabled = false;
			// A second line behind the browser's own switches: nothing but the page's own text and inline data is let through.
			core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
			core.WebResourceRequested += (_, e) =>
			{
				if (e.Request.Uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || e.Request.Uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) return;
				e.Response = environment.CreateWebResourceResponse(null, 403, "Blocked", "");
			};
			await LoadAsync(core, html, cancellation);
			var settings = environment.CreatePrintSettings();
			settings.ShouldPrintBackgrounds = true;
			try
			{
				if (!await core.PrintToPdfAsync(pdfPath, settings).AsTask().WaitAsync(Timeout, cancellation)) throw new HtmlPdfException("Chromium could not print the page to a PDF");
			}
			catch (TimeoutException)
			{
				throw new HtmlPdfException($"Printing took longer than {Timeout.TotalSeconds:0} seconds, so Zing stopped it");
			}
		}
		finally
		{
			view?.Close();
			await DiscardAsync(profile);
		}
	}

	private static async Task LoadAsync(CoreWebView2 core, string html, CancellationToken cancellation)
	{
		var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		void Completed(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs e)
		{
			sender.NavigationCompleted -= Completed;
			if (e.IsSuccess) done.TrySetResult();
			else done.TrySetException(new InvalidOperationException($"The page didn't load: {e.WebErrorStatus}"));
		}
		core.NavigationCompleted += Completed;
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
		timeout.CancelAfter(Timeout);
		using var stop = timeout.Token.Register(() => done.TrySetCanceled(timeout.Token));
		core.NavigateToString(html);
		await done.Task;
	}

	/// <summary>A crash can leave a profile folder. Run once when Zing starts, before any print: a print's own folder is never another print's leftover.</summary>
	public static void SweepLeftovers()
	{
		// Reported through the same path as other host faults; it must not reach OpenRunner's catch, which would switch engines off.
		try
		{
			if (!Directory.Exists(AppPaths.PrintScratch)) return;
			foreach (var leftover in Directory.GetDirectories(AppPaths.PrintScratch))
			{
				try
				{
					Directory.Delete(leftover, recursive: true);
				}
				catch (Exception error) when (error is IOException or UnauthorizedAccessException)
				{
					TestHooks.Record("print scratch", error);
				}
			}
		}
		catch (Exception error) when (error is IOException or UnauthorizedAccessException)
		{
			TestHooks.Record("print scratch", error);
		}
	}

	/// <summary>The browser process may hold the folder for a moment after its view closes; a folder that stays is swept the next time Zing starts.</summary>
	private static async Task DiscardAsync(string folder)
	{
		for (var attempt = 0; attempt < 10; attempt++)
		{
			try
			{
				Directory.Delete(folder, recursive: true);
				return;
			}
			catch (Exception error) when (error is IOException or UnauthorizedAccessException)
			{
				await Task.Delay(200);
			}
		}
		TestHooks.Record("print scratch", new IOException($"Couldn't remove {folder}; Zing removes it the next time it starts"));
	}
}
