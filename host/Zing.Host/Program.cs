using Microsoft.UI.Xaml;
using Microsoft.Web.WebView2.Core;
using WinRT;
using Zing.Core;

namespace Zing.Host;

public static class Program
{
	[STAThread]
	public static void Main()
	{
		TestHooks.RecordUnhandledErrors();
		ComWrappersSupport.InitializeComWrappers();
		Application.Start(_ => new ZingApp());
	}
}

/// <summary>
/// The app: what every window shares (settings, library, session saving, one
/// WebView2 environment) and the list of open windows, oldest first
/// (index.ts and windows.ts).
/// </summary>
public sealed class ZingApp : Application
{
	private readonly List<MainWindow> _windows = [];
	private readonly List<Action> _onQuit = [];
	private AppServices? _services;
	private SessionSaver? _session;
	private IReadOnlyList<SessionTab> _pendingRestore = [];
	private Task<CoreWebView2Environment>? _environment;

	public ZingApp()
	{
		// WinUI's XamlControlsResources isn't loaded: the project has no XAML
		// resources (it throws), and Zing's UI is the React renderer anyway.
		UnhandledException += (_, e) => TestHooks.Record("xaml", e.Exception);
	}

	public AppServices Services => _services ?? throw new InvalidOperationException("Zing has not started");

	public Task<CoreWebView2Environment> Environment => _environment ?? throw new InvalidOperationException("Zing has not started");

	/// <summary>The oldest open window. The only one that saves the session, so two windows never overwrite each other.</summary>
	public MainWindow? Primary => _windows.FirstOrDefault();

	protected override void OnLaunched(LaunchActivatedEventArgs args)
	{
		_services = OpenServices();
		_environment = CreateEnvironmentAsync();
		_session = new SessionSaver(AppPaths.Session, _services.Settings, () => (Primary?.Api?.Tabs.Records ?? [], Primary?.Api?.Tabs.ActiveId), new SystemScheduler());
		_onQuit.Add(_session.Flush);
		if (Flag("restoreTabs")) _pendingRestore = Session.Load(AppPaths.Session);
		else Session.Discard(AppPaths.Session);
		NewWindow();
	}

	public MainWindow NewWindow()
	{
		var window = new MainWindow(this);
		_windows.Add(window);
		window.Activate();
		return window;
	}

	/// <summary>Hands the saved tabs to exactly one window, so later windows open blank.</summary>
	public IReadOnlyList<SessionTab> TakePendingRestore()
	{
		var tabs = _pendingRestore;
		_pendingRestore = [];
		return tabs;
	}

	public void TabsChanged(MainWindow window)
	{
		if (window == Primary) _session?.Changed();
	}

	public void Closed(MainWindow window)
	{
		_windows.Remove(window);
		if (_windows.Count > 0) return;
		foreach (var flush in _onQuit) flush();
		Exit();
	}

	private AppServices OpenServices()
	{
		static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		var settings = new SettingsStore(JsonFiles.LoadSettingsFile(AppPaths.Settings));
		var settingsWriter = new DebouncedJsonWriter(AppPaths.Settings, TimeSpan.FromMilliseconds(500), new SystemScheduler());
		settings.Subscribe((s, _) => settingsWriter.Write(Settings.Changed(s)));
		_onQuit.Add(settingsWriter.Flush);

		DebouncedJsonWriter? libraryWriter = null;
		var library = LibraryStore.Open(AppPaths.Library, path =>
		{
			libraryWriter = new DebouncedJsonWriter(path, TimeSpan.FromMilliseconds(500), new SystemScheduler());
			return snapshot => libraryWriter.Write(snapshot);
		}, Now, () => Guid.NewGuid().ToString());
		_onQuit.Add(() => libraryWriter?.Flush());
		return new AppServices(settings, library, Address.FromDefaults(), Now);
	}

	private bool Flag(string key) => Services.Settings.Get(key)?.GetValue<bool>() ?? throw new InvalidDataException($"Setting {key} is missing");

	private static async Task<CoreWebView2Environment> CreateEnvironmentAsync()
	{
		var options = new CoreWebView2EnvironmentOptions();
		if (TestHooks.DebuggingPort is { } port) options.AdditionalBrowserArguments = $"--remote-debugging-port={port}";
		var userData = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "Zing", TestHooks.IsEndToEndRun ? $"e2e-{System.Environment.ProcessId}" : "WebView2");
		return await CoreWebView2Environment.CreateWithOptionsAsync(null, userData, options);
	}
}
