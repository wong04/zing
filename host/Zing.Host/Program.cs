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
		// Without Edge's WebView2 runtime there is nothing to draw with, not even a notice.
		if (!EngineRuntime.IsInstalled())
		{
			EngineRuntime.ReportMissing();
			return;
		}
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
	private Microsoft.UI.Dispatching.DispatcherQueue? _dispatcher;
	private Task<CoreWebView2Environment>? _environment;
	private EngineRebuild? _rebuild;
	private bool _updateNoticeGiven;

	public AppServices Services => _services ?? throw new InvalidOperationException("Zing has not started");

	public Task<CoreWebView2Environment> Environment => _environment ?? throw new InvalidOperationException("Zing has not started");

	/// <summary>The oldest open window. The only one that saves the session, so two windows never overwrite each other.</summary>
	public MainWindow? Primary => _windows.FirstOrDefault();

	/// <summary>Browsing data on disk (opt-in) instead of in memory; chosen at launch, so a change applies on the next one.</summary>
	public bool PersistentProfile { get; private set; }

	public void Launch()
	{
		var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
		_dispatcher = dispatcher;
		// Debounced saves run on the UI thread, where the stores they read are edited; none of them lock.
		_scheduler = new DispatcherScheduler(dispatcher);
		_services = OpenServices();
		// Any view can be the first to learn the engine's browser process died; the rebuild runs after its event returns.
		EngineFailures.BrowserLost += () => Faults.Enqueue(dispatcher, LoseEngine, "engine rebuild");
		_environment = OpenEnvironmentAsync();
		_session = new SessionSaver(AppPaths.Session, _services.Settings, () => Primary?.Api?.Tabs is { } tabs ? Session.ToSessionTabs(tabs.Records, tabs.ActiveId, tabs.Splits.All) : [], _scheduler, e => TestHooks.Record("session write", e));
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
		// The windows went with a dead engine, not because the person quit: Zing comes back instead.
		if (_rebuild is { } rebuild)
		{
			if (rebuild.Exited) Rebuild();
			return;
		}
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
		return new AppServices(settings, library, Address.FromDefaults(), clock, account, new Zing.Core.Net.RelayConsent(), siteData, purger, TestHooks.NetServices(), new Zing.Core.Net.DnsService(TestHooks.DnsSystem() ?? new DnsSetup(), AppPaths.DnsBackup, () => DateTimeOffset.FromUnixTimeMilliseconds(Now())));
	}

	/// <summary>Opens the engine and listens to it: a browser process that dies, and a runtime update waiting for the next launch.</summary>
	private async Task<CoreWebView2Environment> OpenEnvironmentAsync()
	{
		var environment = await CreateEnvironmentAsync(Flag("acceleratedCanvas"));
		environment.BrowserProcessExited += OnBrowserProcessExited;
		environment.NewBrowserVersionAvailable += OnNewBrowserVersionAvailable;
		return environment;
	}

	/// <summary>A new WebView2 runtime is installed; this engine keeps the old one until Zing restarts. Said once.</summary>
	private void OnNewBrowserVersionAvailable(CoreWebView2Environment sender, object args)
	{
		if (_updateNoticeGiven) return;
		_updateNoticeGiven = true;
		// The restart is the person's to choose; whatever they do next, the session on disk is current (when restore is on).
		_session?.Flush();
		const string notice = "An update to Zing's web engine is ready; it applies the next time you start Zing.";
		if (Primary is { Api: not null } primary) primary.Notify(notice);
		else _startupNotices.Add(notice);
	}

	/// <summary>
	/// The engine's browser process is gone. A normal exit follows the last window closing; a failed one
	/// took every view of every window with it (Microsoft: close all controllers, wait for this event,
	/// then create a new environment).
	/// </summary>
	private void OnBrowserProcessExited(CoreWebView2Environment sender, CoreWebView2BrowserProcessExitedEventArgs e)
	{
		if (e.BrowserProcessExitKind != CoreWebView2BrowserProcessExitKind.Failed) return;
		// An engine Zing has already replaced has nothing left to rebuild.
		if (_environment is not { IsCompletedSuccessfully: true } current || current.Result != sender) return;
		LoseEngine();
		if (_rebuild is not { } rebuild) return;
		rebuild.Exited = true;
		if (_windows.Count == 0) Rebuild();
	}

	/// <summary>
	/// The first step of coming back from a dead engine: keep the primary window's tabs in memory (nothing
	/// new goes to disk), save the session as it stood, and close every window, whose views are all dead.
	/// </summary>
	private void LoseEngine()
	{
		if (_rebuild is not null) return;
		TestHooks.Record("engine", new EngineStoppedException());
		var tabs = Primary?.Api?.Tabs;
		var rebuild = new EngineRebuild(tabs is null ? [] : Session.ToSessionTabs(tabs.Records, tabs.ActiveId, tabs.Splits.All));
		_rebuild = rebuild;
		_session?.Flush();
		// If the engine never says it has exited, Zing still comes back rather than idling with no window.
		rebuild.Deadline = _scheduler?.Schedule(EngineRebuild.ExitWait, Rebuild);
		foreach (var window in _windows.ToList()) window.CloseWindow();
	}

	/// <summary>A new engine and one window with the primary window's tabs, through the same path as session restore.</summary>
	private void Rebuild()
	{
		if (_rebuild is not { } rebuild) return;
		_rebuild = null;
		rebuild.Deadline?.Dispose();
		_pendingRestore = rebuild.Tabs;
		_environment = OpenEnvironmentAsync();
		_startupNotices.Add("Zing's web engine stopped unexpectedly, so Zing restarted it.");
		NewWindow();
	}

	private CoreWebView2? AnyLivePage() => _windows.Select(w => w.AnyLivePage()).FirstOrDefault(core => core is not null);

	private bool Flag(string key) => Services.Settings.Get(key)?.GetValue<bool>() ?? throw new InvalidDataException($"Setting {key} is missing");

	/// <param name="acceleratedCanvas">The acceleratedCanvas setting; like Electron's, it applies from the next launch.</param>
	private static async Task<CoreWebView2Environment> CreateEnvironmentAsync(bool acceleratedCanvas)
	{
		var options = new CoreWebView2EnvironmentOptions();
		options.AdditionalBrowserArguments = string.Join(" ", new[] { TestHooks.BrowserArguments(), acceleratedCanvas ? "" : "--disable-accelerated-2d-canvas" }.Where(a => a.Length > 0));
		// zing://home parses with a host, as Electron's standard scheme did. The getter hands back a copy, so assign the whole list.
		options.CustomSchemeRegistrations = new List<CoreWebView2CustomSchemeRegistration> { new(StartPage.Scheme) { TreatAsSecure = 1, HasAuthorityComponent = true }, new(Zing.Core.Net.RelayedContent.Scheme) { TreatAsSecure = 1, HasAuthorityComponent = true } };
		// The engine's own data; an e2e run keeps it in its scratch profile, so a relaunch finds it and nothing is left in AppData.
		var userData = TestHooks.IsEndToEndRun ? Path.Combine(AppPaths.UserData, "WebView2") : Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "Zing", "WebView2");
		return await CoreWebView2Environment.CreateWithOptionsAsync(null, userData, options);
	}
}

/// <summary>A rebuild in progress: the tabs to bring back, and whether the old engine has finished exiting.</summary>
internal sealed class EngineRebuild(IReadOnlyList<SessionTab> tabs)
{
	/// <summary>How long Zing waits for the dead engine to release its data folder before starting a new one anyway.</summary>
	public static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(10);

	public IReadOnlyList<SessionTab> Tabs { get; } = tabs;
	public bool Exited { get; set; }
	public IDisposable? Deadline { get; set; }
}

/// <summary>
/// How Zing treats a WebView2 process that failed (CoreWebView2.ProcessFailed), per Microsoft's
/// process-related-events guidance. A page's render process (crashed or hung) is the tab's to show,
/// with Reload; the GPU, utility, sandbox-helper and plug-in processes are restarted by the engine
/// itself, so they are only logged; the browser process takes every view with it, so the app rebuilds.
/// None of these is Zing's own fault: they are warnings in the fault log, never ZING_ERRORS_FILE,
/// except the browser process, which ZingApp records.
/// </summary>
public static class EngineFailures
{
	/// <summary>Some view saw the engine's browser process die; raised once per view, so the handler must be idempotent.</summary>
	public static event Action? BrowserLost;

	public static void ReportBrowserLost() => BrowserLost?.Invoke();

	/// <summary>A warning line in the fault log naming the process and why it went (in the kind, which the log always keeps).</summary>
	public static void Warn(string where, CoreWebView2ProcessFailedEventArgs e) =>
		Faults.Warning($"{where}: {e.ProcessFailedKind} ({e.Reason})", new EngineProcessException(e.ProcessFailedKind, e.Reason, e.ExitCode));
}

/// <summary>Whether the WebView2 runtime is installed, and what Zing says when it isn't.</summary>
internal static class EngineRuntime
{
	/// <summary>Microsoft's Evergreen bootstrapper, which installs the runtime.</summary>
	public const string BootstrapperUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";

	private const uint MB_YESNO = 0x4;
	private const uint MB_ICONERROR = 0x10;
	private const int IDYES = 6;

	public static bool IsInstalled()
	{
		try
		{
			return !string.IsNullOrEmpty(CoreWebView2Environment.GetAvailableBrowserVersionString());
		}
		// No runtime surfaces as "file not found" (HRESULT 0x80070002), which the projection maps to FileNotFoundException.
		catch (Exception error) when (error is FileNotFoundException or System.Runtime.InteropServices.COMException)
		{
			return false;
		}
	}

	/// <summary>An e2e run records it and exits (no dialogs in test runs, AGENTS.md); a real launch asks to open the installer.</summary>
	public static void ReportMissing()
	{
		if (TestHooks.IsEndToEndRun)
		{
			TestHooks.Record("startup", new WebView2RuntimeMissingException());
			return;
		}
		var answer = MessageBoxW(0, $"Zing needs Microsoft Edge WebView2 Runtime, which isn't installed on this PC.\n\nDownload it from Microsoft now?\n{BootstrapperUrl}", "Zing can't start", MB_YESNO | MB_ICONERROR);
		if (answer != IDYES) return;
		try
		{
			System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(BootstrapperUrl) { UseShellExecute = true })?.Dispose();
		}
		catch (System.ComponentModel.Win32Exception error)
		{
			// No app to open links with; the address is in the message the person just read.
			TestHooks.Record("open runtime installer", error);
		}
	}

	[System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
	private static extern int MessageBoxW(nint owner, string text, string caption, uint type);
}

public sealed class WebView2RuntimeMissingException() : Exception($"The WebView2 runtime is not installed; get it from {EngineRuntime.BootstrapperUrl}");

public sealed class EngineStoppedException() : Exception("The WebView2 browser process failed; Zing is closing its windows and starting a new engine");

public sealed class EngineProcessException(CoreWebView2ProcessFailedKind kind, CoreWebView2ProcessFailedReason reason, int exitCode)
	: Exception($"{kind} ({reason}, exit code {exitCode})");
