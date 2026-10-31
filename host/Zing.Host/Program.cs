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
		if (SingleInstance.RedirectedToRunningZing()) return;
		Application.Start(start =>
		{
			// What the XAML-generated Main does: without it every await on the UI thread
			// resumes on the thread pool, where WinUI and WebView2 calls fail (RPC_E_WRONG_THREAD).
			SynchronizationContext.SetSynchronizationContext(new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()));
			_ = new ZingApp();
		});
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
	private readonly LinkInbox _links = new();
	private TaskbarIcon? _taskbar;
	private MainWindow? _mostRecent;
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

	/// <summary>Browsing data on disk (opt-in) instead of in memory; chosen at launch, so a change applies on the next one.</summary>
	public bool PersistentProfile { get; private set; }

	protected override void OnLaunched(LaunchActivatedEventArgs args)
	{
		_services = OpenServices();
		_environment = CreateEnvironmentAsync();
		_session = new SessionSaver(AppPaths.Session, _services.Settings, () => (Primary?.Api?.Tabs.Records ?? [], Primary?.Api?.Tabs.ActiveId), new SystemScheduler());
		_onQuit.Add(_session.Flush);
		if (Flag("restoreTabs")) _pendingRestore = Session.Load(AppPaths.Session);
		else Session.Discard(AppPaths.Session);
		foreach (var url in LaunchArgs.Urls(System.Environment.GetCommandLineArgs().Skip(1))) _links.Push(url);
		SingleInstance.Listen(OnSecondLaunch);
		_taskbar = new TaskbarIcon(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
		NewWindow();
	}

	/// <summary>The window used last; links from other apps open there.</summary>
	public MainWindow? MostRecent => _mostRecent is { } w && _windows.Contains(w) ? w : _windows.LastOrDefault();

	public void Focused(MainWindow window) => _mostRecent = window;

	/// <summary>A window can take links once its tabs exist; the first such window takes the ones that arrived during startup.</summary>
	public void Ready(MainWindow window)
	{
		if (window == Primary) _links.Attach(OpenLink);
	}

	private void OpenLink(string url)
	{
		var target = MostRecent ?? NewWindow();
		target.OpenLink(url);
		target.Reveal();
	}

	/// <summary>Another launch handed over its command line: open its links, or just bring Zing forward.</summary>
	private void OnSecondLaunch(IReadOnlyList<string> argv)
	{
		var urls = LaunchArgs.Urls(argv);
		foreach (var url in urls) _links.Push(url);
		if (urls.Count == 0) MostRecent?.Reveal();
	}

	public MainWindow NewWindow()
	{
		var window = new MainWindow(this);
		_windows.Add(window);
		_taskbar?.Add(window.AppWindow);
		window.Start();
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
		_taskbar?.Remove(window.AppWindow);
		if (_windows.Count > 0) return;
		foreach (var flush in _onQuit) flush();
		Exit();
	}

	private AppServices OpenServices()
	{
		var clock = TestHooks.Clock;
		long Now() => clock.Now();
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
		// A persistent profile exists so sign-ins survive closing a tab and quitting; purging would undo exactly that.
		PersistentProfile = settings.Get("persistentProfile")?.GetValue<bool>() ?? throw new InvalidDataException("Setting persistentProfile is missing");
		var siteData = PersistentProfile ? null : new SiteDataKeeper(new WebViewSitePurger(AnyLivePage, () => _windows.Count > 0), (domain, error) => TestHooks.Record($"purge {domain}", error));
		// Test runs skip the gate unless a test is about the gate; a real launch never does.
		var skipGate = TestHooks.IsEndToEndRun && System.Environment.GetEnvironmentVariable("ZING_SKIP_ACCOUNT_GATE") == "1";
		var account = new AccountStore(Path.Combine(AppPaths.UserData, "account"), new DpapiCipher(), skipGate);
		return new AppServices(settings, library, Address.FromDefaults(), clock, account, siteData);
	}

	private CoreWebView2? AnyLivePage() => _windows.Select(w => w.AnyLivePage()).FirstOrDefault(core => core is not null);

	private bool Flag(string key) => Services.Settings.Get(key)?.GetValue<bool>() ?? throw new InvalidDataException($"Setting {key} is missing");

	private static async Task<CoreWebView2Environment> CreateEnvironmentAsync()
	{
		var options = new CoreWebView2EnvironmentOptions();
		options.AdditionalBrowserArguments = TestHooks.BrowserArguments();
		// zing://home parses with a host, as Electron's standard scheme did. The getter hands back a copy, so assign the whole list.
		options.CustomSchemeRegistrations = new List<CoreWebView2CustomSchemeRegistration> { new(StartPage.Scheme) { TreatAsSecure = 1, HasAuthorityComponent = true } };
		// The engine's own data; an e2e run keeps it in its scratch profile, so a relaunch finds it and nothing is left in AppData.
		var userData = TestHooks.IsEndToEndRun ? Path.Combine(AppPaths.UserData, "WebView2") : Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "Zing", "WebView2");
		return await CoreWebView2Environment.CreateWithOptionsAsync(null, userData, options);
	}
}
