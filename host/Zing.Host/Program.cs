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
		// The UI thread's dispatcher; without its context every await on the UI thread resumes on the
		// thread pool, where WebView2 calls fail (RPC_E_WRONG_THREAD).
		var dispatcher = Microsoft.UI.Dispatching.DispatcherQueueController.CreateOnCurrentThread();
		SynchronizationContext.SetSynchronizationContext(new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(dispatcher.DispatcherQueue));
		new ZingApp().Launch();
		// A plain Win32 message loop, as Microsoft's WebView2Browser sample runs; the last window posts WM_QUIT.
		while (Native.GetMessageW(out var message, 0, 0, 0) > 0)
		{
			Native.TranslateMessage(ref message);
			Native.DispatchMessageW(ref message);
		}
		dispatcher.ShutdownQueue();
	}
}

/// <summary>
/// The app: what every window shares (settings, library, session saving, one
/// WebView2 environment) and the list of open windows, oldest first
/// (index.ts and windows.ts).
/// </summary>
public sealed class ZingApp
{
	private readonly List<MainWindow> _windows = [];
	private readonly List<Action> _onQuit = [];
	private readonly LinkInbox _links = new();
	private TaskbarIcon? _taskbar;
	private MainWindow? _mostRecent;
	private AppServices? _services;
	private SessionSaver? _session;
	private IReadOnlyList<SessionTab> _pendingRestore = [];
	private readonly List<string> _startupNotices = [];
	private IScheduler? _scheduler;
	private Task<CoreWebView2Environment>? _environment;

	public AppServices Services => _services ?? throw new InvalidOperationException("Zing has not started");

	public Task<CoreWebView2Environment> Environment => _environment ?? throw new InvalidOperationException("Zing has not started");

	/// <summary>The oldest open window. The only one that saves the session, so two windows never overwrite each other.</summary>
	public MainWindow? Primary => _windows.FirstOrDefault();

	/// <summary>Browsing data on disk (opt-in) instead of in memory; chosen at launch, so a change applies on the next one.</summary>
	public bool PersistentProfile { get; private set; }

	public void Launch()
	{
		var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
		// Debounced saves run on the UI thread, where the stores they read are edited; none of them lock.
		_scheduler = new DispatcherScheduler(dispatcher);
		_services = OpenServices();
		_environment = CreateEnvironmentAsync(Flag("acceleratedCanvas"));
		_session = new SessionSaver(AppPaths.Session, _services.Settings, () => (Primary?.Api?.Tabs.Records ?? [], Primary?.Api?.Tabs.ActiveId), _scheduler, e => TestHooks.Record("session write", e));
		_onQuit.Add(_session.Flush);
		if (Flag("restoreTabs"))
		{
			var restored = Session.LoadOrRecover(AppPaths.Session);
			_pendingRestore = restored.Value;
			if (restored.Problem is { } problem) StartupProblem("session", "your saved tabs", "without them", problem);
		}
		else DiscardSession();
		foreach (var url in LaunchArgs.Urls(System.Environment.GetCommandLineArgs().Skip(1))) _links.Push(url);
		SingleInstance.Listen(OnSecondLaunch);
		_taskbar = new TaskbarIcon(dispatcher);
		NewWindow();
	}

	/// <summary>The window used last; links from other apps open there.</summary>
	public MainWindow? MostRecent => _mostRecent is { } w && _windows.Contains(w) ? w : _windows.LastOrDefault();

	public void Focused(MainWindow window) => _mostRecent = window;

	/// <summary>A window can take links once its tabs exist; the first such window takes the ones that arrived during startup.</summary>
	public void Ready(MainWindow window)
	{
		if (window != Primary) return;
		_links.Attach(OpenLink);
		// A notice replaces the one before, so problems found at startup are told together.
		if (_startupNotices.Count > 0) window.Notify(string.Join(" ", _startupNotices));
		_startupNotices.Clear();
	}

	/// <summary>
	/// A user data file that couldn't be used: logged, and told to the person once the first window is up.
	/// A warning, not a fault: Zing recovered and says so, so an e2e run with a corrupt file (e2e/userdata.spec.ts) still passes.
	/// </summary>
	private void StartupProblem(string kind, string what, string fallback, LoadProblem problem)
	{
		Faults.Warning($"{kind} load", problem.Error);
		var name = Path.GetFileName(problem.Path);
		_startupNotices.Add(problem.KeptAs is { } kept
			? $"Zing couldn't read {what}, so it started {fallback}. The old file is kept as {Path.GetFileName(kept)}."
			: $"Zing couldn't open {what} ({name}), so it started {fallback}. The file was left as it is.");
	}

	/// <summary>With restore off the file must go; if it can't be deleted now, the next launch tries again.</summary>
	private static void DiscardSession()
	{
		try
		{
			Session.Discard(AppPaths.Session);
		}
		catch (Exception error) when (error is IOException or UnauthorizedAccessException)
		{
			TestHooks.Record("session discard", error);
		}
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
		window.Show();
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
		Native.PostQuitMessage(0);
	}

	private AppServices OpenServices()
	{
		var clock = TestHooks.Clock;
		long Now() => clock.Now();
		var scheduler = _scheduler ?? throw new InvalidOperationException("Launch creates the scheduler first");
		// Names a quarantined file; the e2e clock keeps it deterministic.
		var stamp = DateTimeOffset.FromUnixTimeMilliseconds(Now()).LocalDateTime;
		var loadedSettings = JsonFiles.LoadSettingsOrRecover(AppPaths.Settings, stamp);
		if (loadedSettings.Problem is { } settingsProblem) StartupProblem("settings", "your settings", "with the defaults", settingsProblem);
		var settings = new SettingsStore(loadedSettings.Value);
		// A file that couldn't be read is still there and may be fine, so this launch's changes don't overwrite it.
		if (loadedSettings.Problem is not { OriginalInPlace: true })
		{
			var settingsWriter = new DebouncedJsonWriter(AppPaths.Settings, TimeSpan.FromMilliseconds(500), scheduler, e => TestHooks.Record("settings write", e));
			settings.Subscribe((s, _) => settingsWriter.Write(Settings.Changed(s)));
			_onQuit.Add(settingsWriter.Flush);
		}

		DebouncedJsonWriter? libraryWriter = null;
		var openedLibrary = LibraryStore.Open(AppPaths.Library, path =>
		{
			libraryWriter = new DebouncedJsonWriter(path, TimeSpan.FromMilliseconds(500), scheduler, e => TestHooks.Record("library write", e));
			return snapshot => libraryWriter.Write(snapshot);
		}, Now, () => Guid.NewGuid().ToString(), stamp);
		if (openedLibrary.Problem is { } libraryProblem) StartupProblem("library", "your bookmarks and reading list", "with an empty library", libraryProblem);
		var library = openedLibrary.Value;
		_onQuit.Add(() => libraryWriter?.Flush());
		// A persistent profile exists so sign-ins survive closing a tab and quitting; purging would undo exactly that.
		PersistentProfile = settings.Get("persistentProfile")?.GetValue<bool>() ?? throw new InvalidDataException("Setting persistentProfile is missing");
		// One purger for both jobs: a site's data when its last tab closes, and Settings' Clear session (all of it).
		var purger = new WebViewSitePurger(AnyLivePage, () => _windows.Count > 0);
		var siteData = PersistentProfile ? null : new SiteDataKeeper(purger, (domain, error) => TestHooks.Record($"purge {domain}", error));
		// Test runs skip the gate unless a test is about the gate; a real launch never does.
		var skipGate = TestHooks.IsEndToEndRun && System.Environment.GetEnvironmentVariable("ZING_SKIP_ACCOUNT_GATE") == "1";
		var openedAccount = AccountStore.Open(Path.Combine(AppPaths.UserData, "account"), new DpapiCipher(), stamp, skipGate);
		if (openedAccount.Problem is { } accountProblem) StartupProblem("account", "your Zing account", "without it", accountProblem);
		var account = openedAccount.Value;
		return new AppServices(settings, library, Address.FromDefaults(), clock, account, siteData, purger);
	}

	private CoreWebView2? AnyLivePage() => _windows.Select(w => w.AnyLivePage()).FirstOrDefault(core => core is not null);

	private bool Flag(string key) => Services.Settings.Get(key)?.GetValue<bool>() ?? throw new InvalidDataException($"Setting {key} is missing");

	/// <param name="acceleratedCanvas">The acceleratedCanvas setting; like Electron's, it applies from the next launch.</param>
	private static async Task<CoreWebView2Environment> CreateEnvironmentAsync(bool acceleratedCanvas)
	{
		var options = new CoreWebView2EnvironmentOptions();
		options.AdditionalBrowserArguments = string.Join(" ", new[] { TestHooks.BrowserArguments(), acceleratedCanvas ? "" : "--disable-accelerated-2d-canvas" }.Where(a => a.Length > 0));
		// zing://home parses with a host, as Electron's standard scheme did. The getter hands back a copy, so assign the whole list.
		options.CustomSchemeRegistrations = new List<CoreWebView2CustomSchemeRegistration> { new(StartPage.Scheme) { TreatAsSecure = 1, HasAuthorityComponent = true } };
		// The engine's own data; an e2e run keeps it in its scratch profile, so a relaunch finds it and nothing is left in AppData.
		var userData = TestHooks.IsEndToEndRun ? Path.Combine(AppPaths.UserData, "WebView2") : Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "Zing", "WebView2");
		return await CoreWebView2Environment.CreateWithOptionsAsync(null, userData, options);
	}
}
