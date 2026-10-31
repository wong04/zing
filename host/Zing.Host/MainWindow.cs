using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Zing.Core;

namespace Zing.Host;

/// <summary>
/// One browser window (zing-window.ts): the chrome renderer at the bottom, the
/// active tab's page above it, then the settings surface and the notice layer.
/// Later children of the root grid paint above earlier ones.
/// </summary>
public sealed class MainWindow : Window, IWindowHost, IPageHost
{
	private readonly ZingApp _app;
	private readonly Grid _root = new();
	private readonly Bridge _bridge = new();
	private readonly List<RendererView> _renderers = [];
	private RendererView? _chrome;
	private RendererView? _surface;
	private NoticeLayer? _notices;
	private CoreWebView2Environment? _environment;
	private WebView2Page? _shown;
	private bool _closed;

	public MainWindow(ZingApp app)
	{
		_app = app;
		Title = "Zing";
		SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
		ExtendsContentIntoTitleBar = true;
		Content = _root;
		Place();
		_root.SizeChanged += (_, _) => Relayout();
		Closed += (_, _) => OnClosed();
		_ = StartAsync().ContinueWith(t => TestHooks.Record("startup", t.Exception!), TaskContinuationOptions.OnlyOnFaulted);
	}

	public ChromeApi? Api { get; private set; }

	public IPageHost Pages => this;

	private void Place()
	{
		AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 820));
		if (!TestHooks.IsEndToEndRun) return;
		AppWindow.Move(new Windows.Graphics.PointInt32(-20000, -20000));
		AppWindow.IsShownInSwitchers = false;
	}

	// Construction order (AGENTS.md): every field exists before anything can call back into the window.
	private async Task StartAsync()
	{
		_environment = await _app.Environment;
		// The chrome view joins the grid at once, under every page; its API must be registered before it loads and asks for state.
		var chrome = RendererView.CreateAsync(_root, _environment, _bridge, "chrome", transparent: false);
		_notices = new NoticeLayer(_root, _environment, _bridge, ContentRect, DispatcherQueue);
		_bridge.Register("chrome:titlebar-colors", (JsonElement[] args) =>
		{
			AppWindow.TitleBar.ButtonForegroundColor = ParseColor(args[0].GetProperty("symbolColor").GetString()!);
			return (object?)null;
		});
		Api = new ChromeApi(_bridge, _app.Services, this, typeof(MainWindow).Assembly.GetName().Version?.ToString() ?? "0");
		Api.TabsChanged += () => _app.TabsChanged(this);
		Api.Start(_app.TakePendingRestore(), System.Environment.GetEnvironmentVariable("ZING_START_URL"));
		_chrome = await chrome;
		_renderers.Add(_chrome);
	}

	// MARK: IPageHost

	public IPage Create(IPageEvents events)
	{
		var view = new WebView2 { Visibility = Visibility.Collapsed };
		// Pages go just above the chrome renderer, under every layer.
		_root.Children.Insert(Math.Min(1, _root.Children.Count), view);
		return new WebView2Page(view, events, _environment ?? throw new InvalidOperationException("No WebView2 environment yet"));
	}

	public void Show(IPage page)
	{
		var shown = (WebView2Page)page;
		if (_shown is not null && _shown != shown) _shown.View.Visibility = Visibility.Collapsed;
		_shown = shown;
		PlacePage(shown);
		shown.View.Visibility = Visibility.Visible;
	}

	public void Hide(IPage page)
	{
		var hidden = (WebView2Page)page;
		hidden.View.Visibility = Visibility.Collapsed;
		if (_shown == hidden) _shown = null;
		if (!hidden.IsOpen) _root.Children.Remove(hidden.View);
	}

	// MARK: IWindowHost

	public void Push(string channel, object? payload)
	{
		var json = Bridge.Event(channel, payload);
		void Send()
		{
			foreach (var renderer in _renderers) renderer.Send(json);
		}
		if (DispatcherQueue.HasThreadAccess) Send();
		else DispatcherQueue.TryEnqueue(Send);
	}

	public void Notify(string text)
	{
		if (_notices is null || _closed) return;
		_ = _notices.ShowAsync(text).ContinueWith(t => TestHooks.Record("notice", t.Exception!), TaskContinuationOptions.OnlyOnFaulted);
	}

	public void NewWindow() => _app.NewWindow();

	// Not inside the bridge call that asked: the window takes its renderer views with it.
	public void CloseWindow() => DispatcherQueue.TryEnqueue(Close);

	public void Relayout()
	{
		if (_shown is not null) PlacePage(_shown);
		_notices?.Layout();
		if (Api is null) return;
		if (Api.SettingsOpen) _ = ShowSurfaceAsync().ContinueWith(t => TestHooks.Record("surface", t.Exception!), TaskContinuationOptions.OnlyOnFaulted);
		else if (_surface is not null) _surface.Visible = false;
	}

	public void FocusChrome() => _chrome?.View.Focus(FocusState.Programmatic);

	public void FocusSurface() => _surface?.View.Focus(FocusState.Programmatic);

	// MARK: Layout

	private Rect ContentRect() => new(0, 0, _root.ActualWidth, _root.ActualHeight);

	private void PlacePage(WebView2Page page)
	{
		if (Api is null) return;
		var insets = Api.Insets();
		var content = ContentRect();
		page.View.HorizontalAlignment = HorizontalAlignment.Left;
		page.View.VerticalAlignment = VerticalAlignment.Top;
		page.View.Margin = new Thickness(insets.Left, insets.Top, 0, 0);
		page.View.Width = Math.Max(0, content.Width - insets.Left - insets.Right);
		page.View.Height = Math.Max(0, content.Height - insets.Top);
	}

	/// <summary>The settings sheet covers the whole window while it is open (panels.ts surfaceRegion).</summary>
	private async Task ShowSurfaceAsync()
	{
		if (_surface is null)
		{
			_surface = await RendererView.CreateAsync(_root, _environment!, _bridge, "surface", transparent: true);
			_renderers.Add(_surface);
		}
		if (_closed || Api is not { SettingsOpen: true }) return;
		_surface.Place(ContentRect());
		Layers.Raise(_root, _surface.View);
		_notices?.Raise();
		_surface.Visible = true;
		FocusSurface();
	}

	private void OnClosed()
	{
		_closed = true;
		Api?.Dispose();
		_notices?.Destroy();
		foreach (var renderer in _renderers) renderer.Close();
		_app.Closed(this);
	}

	private static Windows.UI.Color ParseColor(string hex)
	{
		if (hex.Length != 7 || hex[0] != '#') throw new FormatException($"Expected #RRGGBB, got {hex}");
		return Windows.UI.Color.FromArgb(255, Convert.ToByte(hex[1..3], 16), Convert.ToByte(hex[3..5], 16), Convert.ToByte(hex[5..7], 16));
	}
}
