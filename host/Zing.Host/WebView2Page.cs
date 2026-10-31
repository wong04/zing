using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Zing.Core;

namespace Zing.Host;

/// <summary>
/// A tab's page over a WebView2. The control starts asynchronously, so calls
/// made before it is ready are queued and run in order once it is.
/// </summary>
public sealed class WebView2Page : IPage
{
	private const string ProfileName = "zing-private";

	private readonly IPageEvents _events;
	private readonly List<Action<CoreWebView2>> _queued = [];
	private CoreWebView2? _core;

	public WebView2Page(WebView2 view, IPageEvents events, CoreWebView2Environment environment)
	{
		View = view;
		_events = events;
		Ready = StartAsync(environment);
		_ = Ready.ContinueWith(t => TestHooks.Record("page", t.Exception!), TaskContinuationOptions.OnlyOnFaulted);
	}

	public WebView2 View { get; }

	/// <summary>Completes once the engine has a CoreWebView2 for this page; a popup hands it to the opener's request.</summary>
	public Task Ready { get; }

	public CoreWebView2 Core => _core ?? throw new InvalidOperationException("The page has not started");

	public bool IsOpen { get; private set; } = true;

	public string Url => _core?.Source ?? "";
	public string Title => _core?.DocumentTitle ?? "";
	public bool IsLoading { get; private set; }
	public bool CanGoBack => _core?.CanGoBack ?? false;
	public bool CanGoForward => _core?.CanGoForward ?? false;

	public void Navigate(string url) => Run(core => core.Navigate(url));
	public void GoBack() => Run(core => core.GoBack());
	public void GoForward() => Run(core => core.GoForward());
	public void Reload() => Run(core => core.Reload());
	public void Stop() => Run(core => core.Stop());
	public void Focus() => View.Focus(FocusState.Programmatic);

	public void Close()
	{
		if (!IsOpen) return;
		IsOpen = false;
		_queued.Clear();
		View.Close();
	}

	private void Run(Action<CoreWebView2> action)
	{
		if (!IsOpen) return;
		if (_core is null) _queued.Add(action);
		else action(_core);
	}

	private async Task StartAsync(CoreWebView2Environment environment)
	{
		var options = environment.CreateCoreWebView2ControllerOptions();
		// Keeps nothing: an in-memory profile, as Electron's non-persistent partition was.
		options.IsInPrivateModeEnabled = true;
		options.ProfileName = ProfileName;
		await View.EnsureCoreWebView2Async(environment, options);
		if (!IsOpen) return;
		_core = View.CoreWebView2;
		_core.Profile.PreferredTrackingPreventionLevel = CoreWebView2TrackingPreventionLevel.Strict;
		Wire(_core);
		foreach (var action in _queued) action(_core);
		_queued.Clear();
	}

	private void Wire(CoreWebView2 core)
	{
		core.NavigationStarting += (_, e) =>
		{
			// Links a page offers are cleaned at navigation time, server redirects included.
			if (_events.Redirect(e.Uri) is { } clean)
			{
				e.Cancel = true;
				core.Navigate(clean);
				return;
			}
			IsLoading = true;
			_events.StateChanged();
		};
		core.NavigationCompleted += (_, e) =>
		{
			IsLoading = false;
			if (!e.IsSuccess && e.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled) _events.LoadFailed(e.WebErrorStatus.ToString());
			_events.StateChanged();
		};
		core.SourceChanged += (_, _) => _events.Navigated(core.Source);
		core.DocumentTitleChanged += (_, _) => _events.TitleChanged(core.DocumentTitle);
		core.HistoryChanged += (_, _) => _events.StateChanged();
		core.WindowCloseRequested += (_, _) => _events.Closed();
		core.NewWindowRequested += OnNewWindowRequested;
	}

	/// <summary>
	/// A popup must be the CoreWebView2 the engine asked for, not a fresh tab
	/// loading the same URL, or window.opener is null and OAuth popups (Google,
	/// Microsoft, payment pages) have nowhere to send their result.
	/// </summary>
	private async void OnNewWindowRequested(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs e)
	{
		var deferral = e.GetDeferral();
		try
		{
			var popup = (WebView2Page)_events.Popup(e.Uri, background: false);
			await popup.Ready;
			e.NewWindow = popup.Core;
			e.Handled = true;
		}
		catch (Exception error)
		{
			TestHooks.Record("popup", error);
			throw;
		}
		finally
		{
			deferral.Complete();
		}
	}
}
