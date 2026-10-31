// Phase W0 spike. One WinUI 3 window, Zing's way: page WebView2s in a grid,
// a transparent overlay WebView2 above them, popups adopted as real
// WebView2s (so window.opener survives), zing://home served by the host,
// an InPrivate profile, and optional remote debugging for Playwright.
//
// Environment:
//   ZING_SPIKE_URL     first page (default zing://home/)
//   ZING_SPIKE_TABS    extra background tabs to open, for the memory check
//   ZING_SPIKE_CDP     remote debugging port for Playwright (off by default)
//   ZING_E2E=1         test run: window off-screen, out of the taskbar
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using WinRT;

namespace ZingSpike;

public static class Program
{
	[STAThread]
	public static void Main()
	{
		AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write("domain", e.ExceptionObject);
		try
		{
			ComWrappersSupport.InitializeComWrappers();
			Application.Start(_ => new SpikeApp());
		}
		catch (Exception error)
		{
			Log.Write("main", error);
			throw;
		}
	}
}

/** The spike reports failures to %TEMP%\zing-spike.log, since a WinExe has no console. */
public static class Log
{
	public static void Write(string where, object error) =>
		File.AppendAllText(Path.Combine(Path.GetTempPath(), "zing-spike.log"), $"[{DateTime.Now:O}] {where}: {error}\n\n");
}

public sealed class SpikeApp : Application
{
	private Window? _window;

	public SpikeApp()
	{
		UnhandledException += (_, e) => Log.Write("xaml", e.Exception);
	}

	protected override void OnLaunched(LaunchActivatedEventArgs args)
	{
		try
		{
			_window = new SpikeWindow();
			_window.Activate();
		}
		catch (Exception error)
		{
			Log.Write("launch", error);
			throw;
		}
	}
}

public sealed class SpikeWindow : Window
{
	private static readonly string StartPage = "<!doctype html><title>Zing home</title><h1>zing://home works</h1><p>Served by the host.</p>";

	private readonly Grid _pages = new();
	private readonly Grid _root = new();
	private CoreWebView2Environment? _environment;

	public SpikeWindow()
	{
		Title = "Zing WebView2 spike";
		SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
		_root.Children.Add(_pages);
		Content = _root;
		PlaceWindow();
		_ = StartAsync();
	}

	private void PlaceWindow()
	{
		var appWindow = AppWindow;
		appWindow.Resize(new Windows.Graphics.SizeInt32(1280, 820));
		if (Environment.GetEnvironmentVariable("ZING_E2E") != "1") return;
		// Test runs must not appear on the user's screen (AGENTS.md).
		appWindow.Move(new Windows.Graphics.PointInt32(-20000, -20000));
		appWindow.IsShownInSwitchers = false;
	}

	private async Task StartAsync()
	{
		var options = new CoreWebView2EnvironmentOptions();
		var cdp = Environment.GetEnvironmentVariable("ZING_SPIKE_CDP");
		if (!string.IsNullOrEmpty(cdp)) options.AdditionalBrowserArguments = $"--remote-debugging-port={cdp}";
		var zing = new CoreWebView2CustomSchemeRegistration("zing") { TreatAsSecure = 1, HasAuthorityComponent = true };
		// The getter hands back a copy, so .Add() on it registers nothing; assign the whole list.
		options.CustomSchemeRegistrations = new List<CoreWebView2CustomSchemeRegistration> { zing };
		var userData = Path.Combine(Path.GetTempPath(), "zing-spike-" + Environment.ProcessId);
		_environment = await CoreWebView2Environment.CreateWithOptionsAsync(null, userData, options);
		Log.Write("schemes", string.Join(",", options.CustomSchemeRegistrations.Select(s => $"{s.SchemeName} secure={s.TreatAsSecure} authority={s.HasAuthorityComponent}")));

		var first = await NewPageAsync();
		first.CoreWebView2.Navigate(Environment.GetEnvironmentVariable("ZING_SPIKE_URL") ?? "zing://home/");

		if (int.TryParse(Environment.GetEnvironmentVariable("ZING_SPIKE_TABS"), out var extra))
		{
			for (var i = 0; i < extra; i++)
			{
				var tab = await NewPageAsync();
				tab.Visibility = Visibility.Collapsed;
				tab.CoreWebView2.Navigate($"zing://home/?tab={i}");
			}
		}

		await AddOverlayAsync();
	}

	private async Task<WebView2> NewPageAsync()
	{
		var view = new WebView2();
		_pages.Children.Add(view);
		await view.EnsureCoreWebView2Async(_environment!, PrivateProfile());
		Wire(view);
		return view;
	}

	private CoreWebView2ControllerOptions PrivateProfile()
	{
		var controller = _environment!.CreateCoreWebView2ControllerOptions();
		controller.IsInPrivateModeEnabled = true;
		controller.ProfileName = "zing-private";
		return controller;
	}

	private void Wire(WebView2 view)
	{
		var core = view.CoreWebView2;
		core.Profile.PreferredTrackingPreventionLevel = CoreWebView2TrackingPreventionLevel.Strict;
		core.AddWebResourceRequestedFilter("zing://*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
		core.WebResourceRequested += (_, e) =>
		{
			Log.Write("resource", $"{e.Request.Uri} source={e.RequestedSourceKind}");
			var bytes = System.Text.Encoding.UTF8.GetBytes(StartPage);
			var stream = new MemoryStream(bytes).AsRandomAccessStream();
			e.Response = _environment!.CreateWebResourceResponse(stream, 200, "OK", "Content-Type: text/html; charset=utf-8");
		};
		core.NewWindowRequested += async (_, e) =>
		{
			// The popup must be the WebView2 Chromium asked for, or window.opener is null.
			var deferral = e.GetDeferral();
			var popup = await NewPageAsync();
			e.NewWindow = popup.CoreWebView2;
			e.Handled = true;
			deferral.Complete();
		};
		core.WindowCloseRequested += (_, _) => _pages.Children.Remove(view);
		core.LaunchingExternalUriScheme += (_, e) => Log.Write("external-scheme", e.Uri);
		core.NavigationStarting += (_, e) => Log.Write("starting", e.Uri);
		core.NavigationCompleted += (_, e) => Log.Write("navigation", $"{core.Source} success={e.IsSuccess} status={e.WebErrorStatus} http={e.HttpStatusCode}");
	}

	private async Task AddOverlayAsync()
	{
		// A transparent WebView2 stacked above the page: how Zing draws UI over web content.
		var overlay = new WebView2
		{
			DefaultBackgroundColor = Microsoft.UI.Colors.Transparent,
			Width = 360,
			Height = 120,
			HorizontalAlignment = HorizontalAlignment.Right,
			VerticalAlignment = VerticalAlignment.Top,
			Margin = new Thickness(0, 16, 16, 0),
		};
		_root.Children.Add(overlay);
		await overlay.EnsureCoreWebView2Async(_environment!);
		overlay.CoreWebView2.NavigateToString("<!doctype html><title>zing-overlay</title><body style='margin:0;background:transparent'><div id=card style='font:14px Segoe UI;background:#6b4cf6;color:white;padding:16px;border-radius:12px'>Overlay above the page</div>");
	}
}
