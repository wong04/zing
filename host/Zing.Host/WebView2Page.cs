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
	private readonly Func<string, StartPageResponse> _serveStartPage;
	private readonly Func<string> _theme;
	private readonly IPageFeatureHost _features;
	private readonly List<Action<CoreWebView2>> _queued = [];
	private CoreWebView2? _core;
	private string? _findTerm;
	private int _textSize = Zing.Core.TextSize.Default;

	public WebView2Page(WebView2 view, IPageEvents events, CoreWebView2Environment environment, Func<string, StartPageResponse> serveStartPage, Func<string> theme, IPageFeatureHost features)
	{
		View = view;
		_events = events;
		_serveStartPage = serveStartPage;
		_theme = theme;
		_features = features;
		Ready = StartAsync(environment);
		_ = Ready.ContinueWith(t => TestHooks.Record("page", t.Exception!), TaskContinuationOptions.OnlyOnFaulted);
	}

	public WebView2 View { get; }

	/// <summary>Completes once the engine has a CoreWebView2 for this page; a popup hands it to the opener's request.</summary>
	public Task Ready { get; }

	public CoreWebView2 Core => _core ?? throw new InvalidOperationException("The page has not started");

	public bool IsOpen { get; private set; } = true;

	public bool IsStarted => _core is not null;

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
	public void Run(string script) => Run(core => _ = core.ExecuteScriptAsync(script));

	public void Find(string query, FindDirection direction, FindMode mode) => Run(core =>
	{
		if (mode == FindMode.Restart || _findTerm != query)
		{
			_findTerm = query;
			var options = core.Environment.CreateFindOptions();
			options.FindTerm = query;
			options.ShouldHighlightAllMatches = true;
			// Zing draws its own find bar.
			options.SuppressDefaultFindDialog = true;
			_ = core.Find.StartAsync(options).AsTask().ContinueWith(t => TestHooks.Record("find", t.Exception!), TaskContinuationOptions.OnlyOnFaulted);
			return;
		}
		if (direction == FindDirection.Next) core.Find.FindNext();
		else core.Find.FindPrevious();
	});

	public int TextSize
	{
		get => _textSize;
		set
		{
			_textSize = value;
			Run(core => _ = core.ExecuteScriptAsync(Zing.Core.TextSize.Script(value)));
		}
	}

	public async Task<string?> CaptureAsync()
	{
		if (!IsOpen || _core is null) return null;
		using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
		await _core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Jpeg, stream);
		if (stream.Size == 0) return null;
		using var memory = new MemoryStream();
		stream.Seek(0);
		await stream.AsStreamForRead().CopyToAsync(memory);
		return "data:image/jpeg;base64," + Convert.ToBase64String(memory.ToArray());
	}

	/// <summary>Edge's own print preview, inside the page.</summary>
	public void Print() => Run(core => core.ShowPrintUI(CoreWebView2PrintDialogKind.Browser));

	public bool IsAudible => _core?.IsDocumentPlayingAudio ?? false;

	public async Task<System.Text.Json.JsonElement> EvaluateAsync(string script)
	{
		if (!IsOpen || _core is null) throw new InvalidOperationException("The page is not running");
		var json = await _core.ExecuteScriptAsync(script);
		return System.Text.Json.JsonDocument.Parse(json).RootElement;
	}

	public void RunWhenLoaded(string script) => Run(core =>
	{
		void Loaded(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs e)
		{
			core.NavigationCompleted -= Loaded;
			if (e.IsSuccess) _ = core.ExecuteScriptAsync(script);
		}
		core.NavigationCompleted += Loaded;
	});

	public void StopFind() => Run(core =>
	{
		_findTerm = null;
		core.Find.Stop();
	});

	/// <summary>The start page draws both palettes and picks one by prefers-color-scheme, which follows Zing's theme setting.</summary>
	public void FollowTheme() => Run(core => core.Profile.PreferredColorScheme = _theme() switch
	{
		"light" => CoreWebView2PreferredColorScheme.Light,
		"dark" => CoreWebView2PreferredColorScheme.Dark,
		_ => CoreWebView2PreferredColorScheme.Auto,
	});

	public void Close()
	{
		if (!IsOpen) return;
		IsOpen = false;
		_features.Left(this);
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
		_core.AddWebResourceRequestedFilter(StartPage.Scheme + "://*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
		_core.WebResourceRequested += OnWebResourceRequested;
		FollowTheme();
		Wire(_core);
		foreach (var action in _queued) action(_core);
		_queued.Clear();
	}

	private void Wire(CoreWebView2 core)
	{
		core.NavigationStarting += (_, e) =>
		{
			// Links a page offers are cleaned at navigation time, server redirects included.
			switch (_events.Navigating(core.Source, e.Uri))
			{
				case NavigationDecision.Cancelled:
					e.Cancel = true;
					return;
				case NavigationDecision.Redirect redirect:
					e.Cancel = true;
					core.Navigate(redirect.Url);
					return;
			}
			// NavigationStarting is only raised for new documents; in-page navigations keep their grants.
			_features.Left(this);
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
		// The override dies with each document, so every new one gets it again.
		core.DOMContentLoaded += (_, _) =>
		{
			if (_textSize != Zing.Core.TextSize.Default) _ = core.ExecuteScriptAsync(Zing.Core.TextSize.Script(_textSize));
		};
		core.DocumentTitleChanged += (_, _) => _events.TitleChanged(core.DocumentTitle);
		core.FaviconChanged += async (_, _) =>
		{
			try
			{
				_events.IconChanged(string.IsNullOrEmpty(core.FaviconUri) ? null : await IconDataUrlAsync(core));
			}
			catch (Exception error)
			{
				TestHooks.Record("favicon", error);
				throw;
			}
		};
		core.HistoryChanged += (_, _) => _events.StateChanged();
		core.WindowCloseRequested += (_, _) => _events.Closed();
		core.NewWindowRequested += OnNewWindowRequested;
		core.DownloadStarting += (_, e) => _features.DownloadStarting(this, e);
		core.PermissionRequested += (_, e) => _features.PermissionRequested(this, e);
		// ActiveMatchIndex already counts from 1, as the find bar does; it is -1 with no match.
		core.Find.MatchCountChanged += (_, _) => _events.FindResult(core.Find.MatchCount, Math.Max(0, core.Find.ActiveMatchIndex));
		core.Find.ActiveMatchIndexChanged += (_, _) => _events.FindResult(core.Find.MatchCount, Math.Max(0, core.Find.ActiveMatchIndex));
	}

	/// <summary>The engine has already chosen and fetched the icon; Zing keeps it as a PNG data URL in memory.</summary>
	private static async Task<string?> IconDataUrlAsync(CoreWebView2 core)
	{
		using var stream = await core.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png);
		if (stream is null || stream.Size == 0) return null;
		using var memory = new MemoryStream();
		await stream.AsStreamForRead().CopyToAsync(memory);
		return "data:image/png;base64," + Convert.ToBase64String(memory.ToArray());
	}

	/// <summary>zing://home is generated by Zing; any other zing: page does not exist.</summary>
	private void OnWebResourceRequested(CoreWebView2 sender, CoreWebView2WebResourceRequestedEventArgs e)
	{
		CoreWebView2WebResourceResponse response;
		try
		{
			var page = _serveStartPage(e.Request.Uri);
			var body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(page.Body)).AsRandomAccessStream();
			response = sender.Environment.CreateWebResourceResponse(body, page.Status, "OK", $"Content-Type: {page.ContentType}\r\nCache-Control: {page.CacheControl}");
		}
		catch (StartPageSchemeException)
		{
			response = sender.Environment.CreateWebResourceResponse(null, 404, "Not Found", "");
		}
		e.Response = response;
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
