using System.Text.Json;
using System.Text.Json.Nodes;

namespace Zing.Core;

/// <summary>What one window's API needs from the window that shows it. The WinUI host implements it; tests use a fake.</summary>
public interface IWindowHost
{
	IPageHost Pages { get; }
	/// <summary>Sends an event to every Zing renderer view of this window.</summary>
	void Push(string channel, object? payload);
	void Notify(string text);
	void NewWindow();
	void CloseWindow();
	/// <summary>The chrome insets or the surface changed; put the page and layers where they now go.</summary>
	void Relayout();
	void FocusChrome();
	void FocusSurface();
	/// <summary>Shows a native menu at a point of the window content, in DIPs.</summary>
	void ShowMenu(IReadOnlyList<MenuItem> items, double x, double y);
	/// <summary>The find bar, downloads and permission prompts changed; show, size or hide their layer.</summary>
	void PageUiChanged(PageUiState state);
	/// <summary>The page UI renderer measured what it shows.</summary>
	void SetPageUiHeight(int height);
	void FocusPageUi();
	/// <summary>A background job failed with no caller to throw to.</summary>
	void Report(string what, Exception error);
	/// <summary>Where downloads are saved, and the file actions on them.</summary>
	string DownloadsDirectory { get; }
	IDownloadHost DownloadHost { get; }
	/// <summary>Runs delayed work on the window's UI thread.</summary>
	IScheduler Scheduler { get; }
	IShellActions Shell { get; }
}

/// <summary>What every window shares: one settings store, one library, one address rule.</summary>
/// <param name="Clock">The app's clock; an e2e run swaps in an <see cref="OffsetClock"/> it can move forward.</param>
public sealed record AppServices(SettingsStore Settings, LibraryStore Library, Address Address, IClock Clock, SiteDataKeeper? SiteData = null)
{
	public long Now() => Clock.Now();
}

/// <summary>
/// One window's answers to its renderer views' bridge calls, and the window's
/// tabs (zing-window.ts, ipc-handlers.ts and command-handlers.ts). Channels not
/// ported yet answer NotImplementedError from the bridge; commands not ported
/// yet say so out loud.
/// </summary>
public sealed class ChromeApi : IDisposable
{
	public const string BlankUrl = StartPage.Url;

	private static readonly HashSet<string> NavigableSchemes = new(StringComparer.OrdinalIgnoreCase) { "http", "https", "about", "file" };

	private readonly AppServices _app;
	private readonly IWindowHost _host;

	internal IWindowHost Host => _host;
	private readonly string _appVersion;
	private readonly List<IDisposable> _subscriptions = [];
	private readonly Action _unsubscribeLibrary;
	private string? _libraryPanel;
	private bool _settingsOpen;
	private string _settingsCategory = "general";

	public TabService Tabs { get; }

	public TabOrdering Ordering { get; }

	public PageUiStore PageUi { get; } = new();

	public FindController Find { get; }

	public DownloadList Downloads { get; }

	public PermissionBroker Permissions { get; }

	public TabLifecycle Lifecycle { get; }

	/// <summary>Fires after any change to this window's tabs, e.g. so the primary window's session can be saved.</summary>
	public event Action? TabsChanged;

	public ChromeApi(Bridge bridge, AppServices app, IWindowHost host, string appVersion)
	{
		_app = app;
		_host = host;
		_appVersion = appVersion;
		Tabs = new TabService(new TabStore(app.Now), host.Pages, BlankUrl, OnTabsChanged, (tab, description) => host.Notify($"Couldn't load {tab.Url} ({description})"), InterceptNavigation, app.SiteData);
		Ordering = new TabOrdering(Tabs, app.Settings, host.Notify, host.Scheduler);
		Find = new FindController(PageUi, new FindHost(this));
		Downloads = new DownloadList(host.DownloadsDirectory, host.DownloadHost, PageUi, host.Notify);
		Permissions = new PermissionBroker(PageUi);
		Lifecycle = new TabLifecycle(new LifecycleTabs(Tabs), app.Settings, app.Clock, host.Scheduler, error => host.Report("tab lifecycle", error));
		Tabs.Woke += (id, page) =>
		{
			if (Lifecycle.Woke(id) is { } y) page.RunWhenLoaded($"window.scrollTo(0, {y.ToString(System.Globalization.CultureInfo.InvariantCulture)})");
		};
		Tabs.FindResult += (page, matches, active) => Find.OnResult(page, matches, active);
		PageUi.Changed += state =>
		{
			host.Push("pageui:state", state);
			host.PageUiChanged(state);
		};

		bridge.Register("tabs:get", _ => State());
		bridge.Register("tabs:activate", args => Do(() => Tabs.Activate(Text(args, 0))));
		bridge.Register("tabs:close", args => Do(() => Tabs.Close(Text(args, 0))));
		bridge.Register("tabs:navigate", args => Do(() => Navigate(Text(args, 0), Text(args, 1))));
		bridge.Register("tabs:move", args => Do(() => Ordering.Move(Text(args, 0), Int(args, 1))));
		bridge.Register("tabs:move-stack", args => Do(() => Ordering.MoveStack(Text(args, 0), Int(args, 1))));
		bridge.Register("tabs:move-within", args => Do(() => Ordering.MoveWithin(Text(args, 0), Text(args, 1), Int(args, 2))));
		bridge.Register("tabs:set-organization", args => Do(() => Ordering.SetOrganization(TabOrganizations.Parse(Text(args, 0)))));
		bridge.Register("tabs:set-dropdowns", args => Do(() => Ordering.SetDropdowns(Bool(args, 0))));
		bridge.Register("chrome:strip-menu", args => Do(() => Menu(Menus.Strip(Ordering), args, 0)));
		bridge.Register("chrome:stack-menu", args => Do(() => Menu(Menus.Stack(Tabs, Ordering, Text(args, 0), host.Shell), args, 1)));
		bridge.Register("tabs:pin", args => Do(() => Tabs.Pin(Text(args, 0))));
		bridge.Register("tabs:unpin", args => Do(() => Tabs.Unpin(Text(args, 0))));
		bridge.Register("tabs:close-others", args => Do(() => Tabs.CloseOthers(Text(args, 0))));
		bridge.Register("command:run", args => Run(Text(args, 0)));
		bridge.Register("settings:get", _ => app.Settings.Snapshot());
		bridge.Register("settings:set", args => Do(() => SetSetting(Text(args, 0), Arg(args, 1))));
		bridge.Register("library:get", _ => LibraryState());
		bridge.Register("library:apply", args => app.Library.Apply(LibraryStore.ParseLibraryAction(Arg(args, 0))));
		bridge.Register("library:open", args => Do(() => OpenLibraryItem(Text(args, 0), Text(args, 1))));
		bridge.Register("library:open-all", args => Do(() => OpenAll(Text(args, 0))));
		bridge.Register("library:panel", args => Do(() => SetLibraryPanel(OptionalText(args, 0))));
		bridge.Register("shell:get", _ => ShellState());
		bridge.Register("shell:open-settings", args => Do(() => OpenSettings(OptionalText(args, 0))));
		bridge.Register("shell:close-settings", _ => Do(CloseSettings));
		// No account gate yet (W3 ports src/main/account.ts); the window opens signed in.
		bridge.Register("account:get", _ => new { hasAccount = false, signedIn = true, needsPasswordReset = false, requirePasswordOnLaunch = false });
		bridge.Register("chrome:app-menu", args => Do(() => Menu(Menus.App(id => Run(id)), args, 0)));
		bridge.Register("chrome:tab-menu", args => Do(() => Menu(Menus.Tab(Tabs, Text(args, 0), host.Shell), args, 1)));
		bridge.Register("library:folder-menu", args => Do(() => Menu(Menus.LibraryFolder(app.Library, Text(args, 0), id => OpenLibraryItem(id, "current"), OpenAll), args, 1)));
		bridge.Register("library:item-menu", args => Do(() => Menu(Menus.LibraryItem(app.Library, Text(args, 0), OpenLibraryItem, OpenAll), args, 1)));
		bridge.Register("pageui:get", _ => PageUi.State);
		bridge.Register("pageui:height", args => Do(() => host.SetPageUiHeight(Int(args, 0))));
		bridge.Register("find:update", args => Do(() => Find.Update(Text(args, 0))));
		bridge.Register("find:step", args => Do(() => Find.Step(FindDirections.Parse(Text(args, 0)))));
		bridge.Register("find:close", _ => Do(Find.Close));
		bridge.Register("downloads:open", args => Do(() => Downloads.Open(Int(args, 0))));
		bridge.Register("downloads:show", args => Do(() => Downloads.Show(Int(args, 0))));
		bridge.Register("downloads:cancel", args => Do(() => Downloads.Cancel(Int(args, 0))));
		bridge.Register("downloads:clear", _ => Do(Downloads.Clear));
		bridge.Register("downloads:dismiss", _ => Do(Downloads.Dismiss));
		bridge.Register("permission:allow", args => Do(() => Permissions.Answer(Int(args, 0), true)));
		bridge.Register("permission:deny", args => Do(() => Permissions.Answer(Int(args, 0), false)));
		bridge.Register("toolpanel:get", _ => new { panels = Array.Empty<object>(), dragging = false });

		_subscriptions.Add(app.Settings.Subscribe(OnSettingChanged));
		_unsubscribeLibrary = app.Library.Subscribe(_ => host.Push("library:state", LibraryState()));
	}

	public bool SettingsOpen => _settingsOpen;

	public TabsState State() => Tabs.State(Setting("tabOrganization"), Flag("tabDropdowns"));

	/// <summary>Where the chrome ends and the page begins. With tabs on the left the app sidebar folds into the rail.</summary>
	public ChromeInsets Insets()
	{
		var position = Setting("tabBarPosition");
		var sidebar = position != "left" && Flag("sidebarPinned") ? Layout.SidebarColumnWidth : 0;
		var panel = _libraryPanel is null ? 0 : Layout.LibraryPanelWidth;
		return Layout.Insets(position, Flag("showFavouritesBar"), Layout.RailDefaultWidth, sidebar + panel);
	}

	/// <summary>Opens the first tab, or brings back a saved session.</summary>
	public void Start(IReadOnlyList<SessionTab> restored, string? startUrl)
	{
		if (restored.Count > 0) Tabs.Restore(restored);
		else Tabs.Open(startUrl ?? HomepageUrl());
	}

	/// <summary>A link from another app: a new tab, or the lone blank tab reused. Non-web links are refused.</summary>
	public void OpenLink(string url)
	{
		AssertNavigable(url);
		if (Tabs.Records is [var only] && (only.Url == TabStore.NewTabUrl || StartPage.IsStartPage(only.Url))) Navigate(only.Id, url);
		else Tabs.Open(LinkCleaner.StripTrackers(url));
	}

	/// <summary>
	/// A key pressed in any of the window's views. True when a command took it,
	/// so the page never sees it; a command with nothing to act on lets the key through.
	/// </summary>
	public bool HandleKey(KeyInput input)
	{
		if (Commands.ForInput(input) is not { } command) return false;
		return Run(command.Id) is not Outcome { Status: "not-applicable" };
	}

	public object Run(string command)
	{
		var outcome = RunCommand(command);
		if (outcome is NotImplemented missing) _host.Notify($"{missing.Label} isn't implemented yet");
		return outcome;
	}

	public void Dispose()
	{
		foreach (var subscription in _subscriptions) subscription.Dispose();
		Ordering.Dispose();
		Lifecycle.Dispose();
		_unsubscribeLibrary();
		Tabs.Destroy();
	}

	private sealed record Outcome(string Status);
	private sealed record NotImplemented(string Label) { public string Status => "not-implemented"; }

	private static readonly Outcome Done = new("done");
	private static readonly Outcome NotApplicable = new("not-applicable");

	private object RunCommand(string command)
	{
		var page = Tabs.ActivePage;
		if (command.StartsWith("tab.select", StringComparison.Ordinal) && int.TryParse(command["tab.select".Length..], out var n)) return ActivateIfAny(Tabs.IdForShortcut(n));
		return command switch
		{
			"app.settings" => Act(true, () => OpenSettings(null)),
			"view.sidebar" => Act(true, () => _app.Settings.Set("sidebarPinned", !Flag("sidebarPinned"))),
			"view.favouritesBar" => Act(true, () => _app.Settings.Set("showFavouritesBar", !Flag("showFavouritesBar"))),
			"library.addFavourite" => SaveActive(favourite: true),
			"library.addReadingList" => SaveActive(favourite: false),
			"library.bookmarks" => Act(true, () => SetLibraryPanel(_libraryPanel == "bookmarks" ? null : "bookmarks")),
			"library.readingList" => Act(true, () => SetLibraryPanel(_libraryPanel == "readingList" ? null : "readingList")),
			"tab.new" => Act(true, NewTab),
			"tab.close" => Act(Tabs.ActiveId is not null, () => Tabs.Close(Tabs.ActiveId!)),
			"tab.reopenClosed" => Tabs.ReopenClosed() is null ? NotApplicable : Done,
			"tab.next" => ActivateIfAny(Tabs.NextId()),
			"tab.previous" => ActivateIfAny(Tabs.PreviousId()),
			"window.new" => Act(true, _host.NewWindow),
			"window.close" => Act(true, _host.CloseWindow),
			"location.open" => Act(true, FocusAddress),
			"history.back" => Act(page is { CanGoBack: true }, () => page!.GoBack()),
			"history.forward" => Act(page is { CanGoForward: true }, () => page!.GoForward()),
			"history.home" => Act(Tabs.ActiveId is not null, () => Tabs.Navigate(Tabs.ActiveId!, HomepageUrl())),
			"page.reload" => Act(page is not null, () => page!.Reload()),
			"page.find" => Act(page is not null, Find.Open),
			"page.findNext" => Act(page is not null, () => Find.Step(FindDirection.Next)),
			"page.findPrevious" => Act(page is not null, () => Find.Step(FindDirection.Previous)),
			"page.stop" => Act(page is { IsLoading: true }, () => page!.Stop()),
			_ => new NotImplemented(Commands.Get(command).Label),
		};
	}

	private void NewTab()
	{
		Tabs.Open(BlankUrl);
		FocusAddress();
	}

	private void FocusAddress()
	{
		_host.FocusChrome();
		_host.Push("chrome:focus-address", null);
	}

	private Outcome ActivateIfAny(string? id)
	{
		if (id is null) return NotApplicable;
		Tabs.Activate(id);
		return Done;
	}

	private static Outcome Act(bool applicable, Action action)
	{
		if (!applicable) return NotApplicable;
		action();
		return Done;
	}

	private static object? Do(Action action)
	{
		action();
		return null;
	}

	/// <summary>Takes whatever was typed or a ready URL; blank input does nothing.</summary>
	private void Navigate(string id, string input)
	{
		Tabs.Get(id);
		if (AddressInput.JsTrim(input).Length == 0) return;
		var url = _app.Address.Resolve(input, Setting("searchEngine"));
		try
		{
			AssertNavigable(url);
		}
		catch (UnsupportedUrlException error)
		{
			_host.Notify(error.Message);
			return;
		}
		Tabs.Navigate(id, url);
		Tabs.FocusPage(id);
	}

	private static void AssertNavigable(string url)
	{
		if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) || !NavigableSchemes.Contains(parsed.Scheme)) throw new UnsupportedUrlException(url);
	}

	/// <summary>The configured homepage by the address bar's own rule; blank means a blank tab until the start page is ported.</summary>
	private string HomepageUrl()
	{
		var typed = Setting("homepage");
		return AddressInput.JsTrim(typed).Length == 0 ? BlankUrl : _app.Address.Resolve(typed, Setting("searchEngine"));
	}

	private void SetSetting(string key, JsonElement value)
	{
		if (!SettingsSchema.IsSettingKey(key)) throw new UnknownSettingException(key);
		_app.Settings.Set(key, JsonNode.Parse(value.GetRawText()));
	}

	private void OnSettingChanged(JsonObject settings, string key)
	{
		_host.Push("settings:changed", settings);
		if (key is "tabOrganization" or "tabDropdowns")
		{
			Ordering.Reorganize();
			OnTabsChanged();
		}
		if (key is "accent" or "theme") ForEachStartPage(page => page.Run(StartPage.RestyleScript(StartPage.Theme(Setting("accent")))));
		if (key is "startShortcuts" or "searchEngine") ForEachStartPage(page => page.Reload());
		if (key is "showFavouritesBar") _host.Push("library:state", LibraryState());
		if (key is "sidebarPinned" or "showFavouritesBar" or "tabBarPosition")
		{
			_host.Relayout();
			_host.Push("library:state", LibraryState());
		}
	}

	private object LibraryState() => new
	{
		library = _app.Library.Snapshot,
		panel = _libraryPanel,
		favouritesBar = Flag("showFavouritesBar"),
		offset = Flag("sidebarPinned") ? Layout.SidebarColumnWidth : 0,
	};

	private Outcome SaveActive(bool favourite)
	{
		if (Tabs.ActiveId is not { } id) return NotApplicable;
		var tab = Tabs.Get(id);
		if (!tab.Url.StartsWith("http:", StringComparison.OrdinalIgnoreCase) && !tab.Url.StartsWith("https:", StringComparison.OrdinalIgnoreCase)) return NotApplicable;
		var title = string.IsNullOrWhiteSpace(tab.Title) ? new Uri(tab.Url).Host : tab.Title.Trim();
		var outcome = favourite ? _app.Library.AddFavourite(title, tab.Url) : _app.Library.AddReading(title, tab.Url);
		var where = favourite ? "Favourites" : "Reading List";
		_host.Notify(outcome == AddOutcome.Added ? $"Added to {where}." : $"Already in {where}.");
		return Done;
	}

	private void SetLibraryPanel(string? panel)
	{
		if (panel is not (null or "bookmarks" or "readingList")) throw new ArgumentException($"Unknown library panel {panel}");
		if (_libraryPanel == panel) return;
		_libraryPanel = panel;
		_host.Relayout();
		_host.Push("library:state", LibraryState());
		if (panel is not null) _host.FocusChrome();
	}

	private void OpenLibraryItem(string id, string target)
	{
		if (target is not ("current" or "tab")) throw new ArgumentException($"Unknown library open target {target}");
		if (_app.Library.UrlOf(id) is not { } url) return;
		if (target == "tab" || Tabs.ActiveId is null)
		{
			Tabs.Open(url);
			return;
		}
		Tabs.Navigate(Tabs.ActiveId, url);
		Tabs.FocusPage(Tabs.ActiveId);
	}

	private void OpenAll(string id)
	{
		foreach (var url in _app.Library.UrlsOf(id)) Tabs.Open(url);
	}

	private object ShellState() => new
	{
		settingsOpen = _settingsOpen,
		settingsCategory = _settingsCategory,
		sidebarRevealed = false,
		gated = false,
		origin = new { x = 0, y = 0 },
		appVersion = _appVersion,
	};

	private void OpenSettings(string? category)
	{
		if (category is not null) _settingsCategory = category;
		_settingsOpen = true;
		_host.Relayout();
		_host.Push("shell:state", ShellState());
		_host.FocusSurface();
	}

	private void CloseSettings()
	{
		if (!_settingsOpen) return;
		_settingsOpen = false;
		_host.Relayout();
		_host.Push("shell:state", ShellState());
		if (Tabs.ActiveId is { } id) Tabs.FocusPage(id);
	}

	private void ForEachStartPage(Action<IPage> act)
	{
		foreach (var record in Tabs.Records)
			if (StartPage.IsStartPage(record.Url) && Tabs.PageOf(record.Id) is { } page) act(page);
	}

	/// <summary>The start page's search and settings links are Zing's to handle; no web page may open Settings.</summary>
	private NavigationDecision? InterceptNavigation(string from, string to)
	{
		switch (StartPage.ClassifyNavigation(from, to, Setting("searchEngine")))
		{
			case StartPageNavigation.Search search:
				if (AddressInput.JsTrim(search.Query).Length == 0) return NavigationDecision.Cancel;
				return new NavigationDecision.Redirect(_app.Address.Resolve(search.Query, Setting("searchEngine")));
			case StartPageNavigation.EditShortcuts:
				OpenSettings(null);
				return NavigationDecision.Cancel;
			case StartPageNavigation.Block:
				return NavigationDecision.Cancel;
			default:
				return null;
		}
	}

	private void OnTabsChanged()
	{
		Ordering?.ScheduleReorganize();
		Find?.ActiveChanged();
		Lifecycle?.Reschedule();
		_host.Push("tabs:state", State());
		TabsChanged?.Invoke();
	}

	private string Setting(string key) => _app.Settings.Get(key)?.GetValue<string>() ?? throw new InvalidDataException($"Setting {key} is missing");

	private bool Flag(string key) => _app.Settings.Get(key)?.GetValue<bool>() ?? throw new InvalidDataException($"Setting {key} is missing");

	private void Menu(IReadOnlyList<MenuItem> items, JsonElement[] args, int anchorIndex)
	{
		if (items.Count == 0) return;
		var anchor = Arg(args, anchorIndex);
		_host.ShowMenu(items, Number(anchor, "x"), Number(anchor, "y"));
	}

	private static double Number(JsonElement value, string name) =>
		value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var n) && n.ValueKind == JsonValueKind.Number ? n.GetDouble() : throw new ArgumentException($"Anchor needs a numeric {name}");

	private static JsonElement Arg(JsonElement[] args, int index) =>
		index < args.Length ? args[index] : throw new ArgumentException($"Argument {index} is missing");

	private static string Text(JsonElement[] args, int index)
	{
		var arg = Arg(args, index);
		return arg.ValueKind == JsonValueKind.String ? arg.GetString()! : throw new ArgumentException($"Argument {index} must be a string");
	}

	private static string? OptionalText(JsonElement[] args, int index) =>
		index >= args.Length || args[index].ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null : Text(args, index);

	private static bool Bool(JsonElement[] args, int index)
	{
		var arg = Arg(args, index);
		return arg.ValueKind is JsonValueKind.True or JsonValueKind.False ? arg.GetBoolean() : throw new ArgumentException($"Argument {index} must be a boolean");
	}

	private static int Int(JsonElement[] args, int index)
	{
		var arg = Arg(args, index);
		return arg.ValueKind == JsonValueKind.Number && arg.TryGetInt32(out var value) ? value : throw new ArgumentException($"Argument {index} must be an integer");
	}
}

/// <summary>What sleeping tabs and auto-close need from a window's tabs.</summary>
internal sealed class LifecycleTabs(TabService tabs) : ILifecycleTabs
{
	public string? ActiveId => tabs.ActiveId;
	public IReadOnlyList<TabRecord> Records => tabs.Records;
	public bool HasPage(string id) => tabs.HasPage(id);
	public bool IsAudible(string id) => tabs.PageOf(id)?.IsAudible ?? false;

	public async Task<PageProbe> ProbeAsync(string id)
	{
		var page = tabs.PageOf(id) ?? throw new PageUnavailableException(id);
		try
		{
			return PageProbe.Parse(await page.EvaluateAsync(PageProbe.Source));
		}
		catch (Exception error) when (error is not PageProbeException)
		{
			throw new PageUnavailableException(id, error);
		}
	}

	// The tab overview that shows thumbnails isn't ported yet (W3d); a sleeping tab may have none, as in Electron.
	public Task<string?> CaptureThumbnailAsync(string id) => Task.FromResult<string?>(null);

	public void Sleep(string id) => tabs.Sleep(id);
	public void Close(string id) => tabs.Close(id);
}

/// <summary>The find bar's view of the window: pages are this window's tabs' pages.</summary>
internal sealed class FindHost(ChromeApi api) : IFindHost
{
	public object? ActivePage => api.Tabs.ActivePage;
	public bool IsAlive(object page) => api.Tabs.Records.Any(r => ReferenceEquals(api.Tabs.PageOf(r.Id), page));
	public void Search(object page, string query, FindDirection direction, FindMode mode) => ((IPage)page).Find(query, direction, mode);
	public void ClearSelection(object page) => ((IPage)page).StopFind();
	public void FocusPanel() => api.Host.FocusPageUi();
	public void FocusPage(object page) => ((IPage)page).Focus();
}

public sealed class UnknownCommandException(string command) : Exception($"Unknown command {command}");

public sealed class UnsupportedUrlException(string url) : Exception($"Zing can't open {url}")
{
	public string Url { get; } = url;
}
