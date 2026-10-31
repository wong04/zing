using System.Text.Json.Nodes;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Zing.Core;

namespace Zing.Host;

/// <summary>
/// One Zing window (W1): the chrome renderer (Zing's React UI) fills the
/// window, and the active tab's page WebView2 sits in the page area the
/// chrome leaves free. A WebView2 is its own window, so nothing XAML can draw
/// over it; UI over pages will be further WebView2s, as the W0 spike proved.
/// </summary>
public sealed class MainWindow : Window
{
	private const string AppHost = "app.zing";

	private readonly Grid _root = new();
	private readonly WebView2 _chrome = new();
	private readonly Dictionary<string, WebView2Page> _pages = new(StringComparer.Ordinal);
	private readonly Bridge _bridge = new();
	private readonly TabStore _tabs = new(() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
	private readonly JsonObject _settings = ZingJson.LoadDefaults("settings.json").AsObject();
	private readonly JsonObject _layout = ZingJson.LoadDefaults("layout.json").AsObject();
	private CoreWebView2Environment? _environment;
	private ChromeApi? _api;

	public MainWindow()
	{
		Title = "Zing";
		SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
		ExtendsContentIntoTitleBar = true;
		_root.Children.Add(_chrome);
		Content = _root;
		Place();
		_ = StartAsync().ContinueWith(t => TestHooks.Record("startup", t.Exception!), TaskContinuationOptions.OnlyOnFaulted);
	}

	private void Place()
	{
		AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 820));
		if (!TestHooks.IsEndToEndRun) return;
		AppWindow.Move(new Windows.Graphics.PointInt32(-20000, -20000));
		AppWindow.IsShownInSwitchers = false;
	}

	private async Task StartAsync()
	{
		_environment = await CreateEnvironmentAsync();
		await _chrome.EnsureCoreWebView2Async(_environment);
		var chrome = _chrome.CoreWebView2;
		chrome.Settings.IsNonClientRegionSupportEnabled = true;
		chrome.SetVirtualHostNameToFolderMapping(AppHost, RendererFolder(), CoreWebView2HostResourceAccessKind.Allow);
		chrome.WebMessageReceived += OnChromeMessage;

		_api = new ChromeApi(_bridge, _tabs, Address.FromDefaults(), _settings, ZingJson.LoadDefaults("library.json"), id => _pages[id], Push, "w1");
		_bridge.Register("chrome:titlebar-colors", (System.Text.Json.JsonElement[] args) =>
		{
			var color = args[0].GetProperty("symbolColor").GetString()!;
			AppWindow.TitleBar.ButtonForegroundColor = ParseColor(color);
			return (object?)null;
		});

		var first = _tabs.Open(Environment.GetEnvironmentVariable("ZING_START_URL") ?? "about:blank");
		await AddPageAsync(first.Id, first.Url);
		chrome.Navigate($"https://{AppHost}/index.html#chrome");
	}

	private static async Task<CoreWebView2Environment> CreateEnvironmentAsync()
	{
		var options = new CoreWebView2EnvironmentOptions();
		if (TestHooks.DebuggingPort is { } port) options.AdditionalBrowserArguments = $"--remote-debugging-port={port}";
		var userData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Zing", TestHooks.IsEndToEndRun ? $"e2e-{Environment.ProcessId}" : "WebView2");
		return await CoreWebView2Environment.CreateWithOptionsAsync(null, userData, options);
	}

	private async Task AddPageAsync(string id, string url)
	{
		var view = new WebView2();
		_root.Children.Add(view);
		view.Margin = PageMargin();
		var controller = _environment!.CreateCoreWebView2ControllerOptions();
		// Keeps nothing: an in-memory profile, as Electron's non-persistent partition was.
		controller.IsInPrivateModeEnabled = true;
		controller.ProfileName = "zing-private";
		await view.EnsureCoreWebView2Async(_environment, controller);
		view.CoreWebView2.Profile.PreferredTrackingPreventionLevel = CoreWebView2TrackingPreventionLevel.Strict;
		var page = new WebView2Page(view, () => PageChanged(id));
		_pages[id] = page;
		page.Navigate(url);
	}

	private void PageChanged(string id)
	{
		var page = _pages[id];
		_tabs.Update(id, url: page.Url, title: page.Title);
		_tabs.SetPage(id, new PageState(page.IsLoading, page.CanGoBack, page.CanGoForward));
		_api?.TabsChanged();
	}

	/// <summary>Where the page sits: below the tab strip and toolbar, right of a pinned sidebar (W2 moves this to chromeInsets).</summary>
	private Thickness PageMargin()
	{
		var top = _layout["tabStripHeight"]!.GetValue<int>() + _layout["toolbarHeight"]!.GetValue<int>();
		var left = _settings["sidebarPinned"]!.GetValue<bool>() ? _layout["sidebarColumnWidth"]!.GetValue<int>() : 0;
		return new Thickness(left, top, 0, 0);
	}

	private async void OnChromeMessage(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs e)
	{
		// Only Zing's own UI may talk to the host, never a web page.
		if (!e.Source.StartsWith($"https://{AppHost}/", StringComparison.Ordinal))
		{
			TestHooks.Record("bridge", new UnauthorizedAccessException($"Message from {e.Source} refused"));
			return;
		}
		try
		{
			sender.PostWebMessageAsJson(await _bridge.HandleAsync(e.WebMessageAsJson));
		}
		catch (Exception error)
		{
			TestHooks.Record("bridge", error);
			throw;
		}
	}

	private void Push(string channel, object? payload) =>
		DispatcherQueue.TryEnqueue(() => _chrome.CoreWebView2?.PostWebMessageAsJson(Bridge.Event(channel, payload)));

	/// <summary>
	/// The built React renderer. ZING_RENDERER_DIR wins (tests, packaging);
	/// otherwise the nearest out/renderer above the app, which is where a
	/// development checkout builds it.
	/// </summary>
	private static string RendererFolder()
	{
		var configured = Environment.GetEnvironmentVariable("ZING_RENDERER_DIR");
		if (!string.IsNullOrEmpty(configured)) return RequireIndex(configured);
		for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
		{
			var candidate = Path.Combine(dir.FullName, "out", "renderer");
			if (File.Exists(Path.Combine(candidate, "index.html"))) return candidate;
		}
		throw new DirectoryNotFoundException("No built renderer: run `npm run build`, or set ZING_RENDERER_DIR");
	}

	private static string RequireIndex(string folder) =>
		File.Exists(Path.Combine(folder, "index.html")) ? folder : throw new DirectoryNotFoundException($"{folder} has no index.html");

	private static Windows.UI.Color ParseColor(string hex)
	{
		if (hex.Length != 7 || hex[0] != '#') throw new FormatException($"Expected #RRGGBB, got {hex}");
		return Windows.UI.Color.FromArgb(255, Convert.ToByte(hex[1..3], 16), Convert.ToByte(hex[3..5], 16), Convert.ToByte(hex[5..7], 16));
	}
}

/// <summary>A tab's page: an IPage over a WebView2, reporting every change.</summary>
public sealed class WebView2Page : IPage
{
	private readonly CoreWebView2 _core;

	public WebView2Page(WebView2 view, Action changed)
	{
		_core = view.CoreWebView2;
		_core.NavigationStarting += (_, _) =>
		{
			IsLoading = true;
			changed();
		};
		_core.NavigationCompleted += (_, _) =>
		{
			IsLoading = false;
			changed();
		};
		_core.SourceChanged += (_, _) => changed();
		_core.DocumentTitleChanged += (_, _) => changed();
		_core.HistoryChanged += (_, _) => changed();
	}

	public string Url => _core.Source;
	public string Title => _core.DocumentTitle;
	public bool IsLoading { get; private set; }
	public bool CanGoBack => _core.CanGoBack;
	public bool CanGoForward => _core.CanGoForward;
	public void Navigate(string url) => _core.Navigate(url);
	public void GoBack() => _core.GoBack();
	public void GoForward() => _core.GoForward();
	public void Reload() => _core.Reload();
	public void Stop() => _core.Stop();
}
