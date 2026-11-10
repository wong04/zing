using System.Text.Json;
using System.Text.Json.Nodes;
using Zing.Core.Engines;
using Zing.Core.LinkPreview;
using Zing.Core.Viewer;
using Zing.Core.Net;
using Zing.Core.Sync;
using Zing.Core.Vault;

namespace Zing.Core;

/// <summary>What one window's API needs from the window that shows it. The Win32 host implements it; tests use a fake.</summary>
public interface IWindowHost
{
	IPageHost Pages { get; }
	/// <summary>Sends an event to every Zing renderer view of this window.</summary>
	void Push(string channel, object? payload);
	void Notify(string text);
	/// <summary>Shows the offer to open a link or diagnose a page that failed to load like a block, until it is chosen or dismissed. <paramref name="address"/> is what to show of the failed address.</summary>
	void OfferUnblock(string address);
	/// <summary>Takes the offer down.</summary>
	void DismissOffer();
	/// <summary>The offer left the screen on its own: it timed out, or another notice replaced it.</summary>
	event Action OfferEnded;
	void NewWindow();
	void CloseWindow();
	/// <summary>The chrome insets or the surface changed; put the page and layers where they now go.</summary>
	void Relayout();
	void FocusChrome();
	void FocusSurface();
	/// <summary>The window's content size in DIPs, and its top-left on screen (where the renderer's screen points start).</summary>
	ContentSize ContentSize { get; }
	Point ContentOrigin { get; }
	/// <summary>The cursor over the content, or null when it is outside or Zing is not the focused app.</summary>
	Point? Cursor { get; }
	/// <summary>Whether Windows' high-contrast mode is on.</summary>
	bool HighContrast { get; }
	/// <summary>Where the surface view (settings sheet, account gate, sidebar, panels) goes; null hides it.</summary>
	void SurfaceRegionChanged(Rect? region);
	/// <summary>Shows a native menu at a point of the window content, in DIPs.</summary>
	void ShowMenu(IReadOnlyList<MenuItem> items, double x, double y);
	/// <summary>The find bar, downloads and permission prompts changed; show, size or hide their layer.</summary>
	void PageUiChanged(PageUiState state);
	/// <summary>The page UI renderer measured what it shows.</summary>
	void SetPageUiHeight(int height);
	void FocusPageUi();
	/// <summary>The vault's account picker opened, moved or closed (null); show, place or hide its view.</summary>
	void AccountPickerChanged(AccountPicker? picker);
	/// <summary>The picker view measured what it shows.</summary>
	void SetAccountPickerHeight(int height);
	/// <summary>The stack dropdown opened, changed or closed (null).</summary>
	void StackDropdownChanged(StackDropdownState? state);
	/// <summary>The tab overview opened, changed or closed (null).</summary>
	void OverviewChanged(OverviewState? state);
	/// <summary>Something optional failed (a tab picture); worth a note, not a failure.</summary>
	void Warn(string what, Exception error);
	/// <summary>A background job failed with no caller to throw to.</summary>
	void Report(string what, Exception error);
	/// <summary>Work nobody awaits; its failures are reported.</summary>
	void Background(Task task, string what);
	/// <summary>Where downloads are saved, and the file actions on them.</summary>
	string DownloadsDirectory { get; }
	IDownloadHost DownloadHost { get; }
	/// <summary>Shows the native open dialog for these extensions (none means every file) and returns what the person picked; empty if they cancelled. An e2e run shows no dialog.</summary>
	Task<IReadOnlyList<string>> PickFilesAsync(IReadOnlyList<string> extensions);
	/// <summary>Puts these files on the clipboard as files (CF_HDROP), so Explorer can paste them. Called on the UI thread; throws if the clipboard can't be written. An e2e run leaves the real clipboard alone.</summary>
	void CopyFilesToClipboard(IReadOnlyList<string> paths);
	/// <summary>Where a copied password goes (Ruling 41). An e2e run never touches the real clipboard.</summary>
	ISecretClipboard SecretClipboard { get; }
	/// <summary>Has Chromium print this HTML text to a PDF at this path (Convert). Scripts and the network are off while it does.</summary>
	Task PrintHtmlToPdfAsync(string html, string pdfPath, CancellationToken cancellation);
	/// <summary>Runs delayed work on the window's UI thread.</summary>
	IScheduler Scheduler { get; }
	IShellActions Shell { get; }
}

/// <summary>What every window shares: one settings store, one library, one address rule.</summary>
/// <param name="Clock">The app's clock; an e2e run swaps in an <see cref="OffsetClock"/> it can move forward.</param>
/// <param name="SiteData">Purges a site when its last tab closes; null with a persistent profile.</param>
/// <param name="BrowsingData">Settings' Clear browsing session; null where the host has no engine to clear.</param>
/// <param name="Engines">The downloaded engines (FFmpeg and the rest), shared by every window; null where the host has none.</param>
/// <param name="Runner">Runs them; shared so that "at most 3 jobs" holds for all of Zing. Null where the host has none.</param>
/// <param name="Net">The network checks Diagnose makes; null uses the real ones, a test passes fakes.</param>
/// <param name="Dns">Encrypted DNS for the whole PC (Network &amp; VPN); null where the host can't change DNS.</param>
/// <param name="Threats">Link inspection, with the hosts allowed this session; one per app. Null where the host doesn't inspect links.</param>
/// <param name="Consent">Whether third parties may be asked to open a link, for the session and every window. One per app: required, so a window cannot end up with its own.</param>
/// <param name="Vault">A vault already open that never follows the setting (tests); <paramref name="Switch"/> is the real one.</param>
/// <param name="VaultUnreadable">With <paramref name="Vault"/>: the vault is off because its files couldn't be read.</param>
/// <param name="Switch">The password vault, shared by every window, open only while <c>passwordAutofill</c> is on.</param>
/// <param name="Sync">The sync server and Google sign-in this build has; null when it was built without a Google client ID (Ruling 48).</param>
/// <param name="SyncScheduler">Runs the open vault's sync engine (Task 13); null runs none.</param>
public sealed record AppServices(SettingsStore Settings, LibraryStore Library, Address Address, IClock Clock, AccountStore Account, RelayConsent Consent, SiteDataKeeper? SiteData = null, IBrowsingData? BrowsingData = null, NetServices? Net = null, DnsService? Dns = null, ThreatGate? Threats = null, EngineStore? Engines = null, EngineRunner? Runner = null, string? ViewerRoot = null, VaultStore? Vault = null, bool VaultUnreadable = false, VaultSwitch? Switch = null, SyncSetup? Sync = null, IScheduler? SyncScheduler = null)
{
	public long Now() => Clock.Now();

	/// <summary>The vault as it is now: <see cref="VaultSwitch.Current"/> is null while it's off.</summary>
	public VaultSwitch Vaults { get; } = Switch ?? VaultSwitch.Fixed(Vault, VaultUnreadable);

	/// <summary>The open vault's one sync client, for every window; null without <see cref="Sync"/>.</summary>
	public SyncClients? SyncClients { get; } = Sync is null ? null : new SyncClients(Sync, Switch ?? VaultSwitch.Fixed(Vault, VaultUnreadable), SyncScheduler, Clock);
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
	private readonly RelayConsent _consent;
	/// <summary>The address of the page the offer on screen is for.</summary>
	private string? _offered;
	private string? _offeredTab;
	private readonly IWindowHost _host;

	internal IWindowHost Host => _host;
	private readonly string _appVersion;
	private readonly OutputPlacement _placement;
	private readonly List<IDisposable> _subscriptions = [];
	private readonly Action _unsubscribeLibrary;
	private string? _libraryPanel;
	// Cancelled when the window closes, so a download it started doesn't outlive it.
	private readonly CancellationTokenSource _closing = new();
	// This window's downloads by engine id, so Cancel can stop one.
	private readonly Dictionary<string, CancellationTokenSource> _downloads = new(StringComparer.Ordinal);
	// Names this window's jobs to the runner, which is shared by every window.
	private readonly string _windowId = Guid.NewGuid().ToString("N");
	private volatile bool _disposed;

	/// <summary>The files this window's renderer may give an engine: what the picker or a drop issued.</summary>
	public FileGrants Grants { get; } = new();

	/// <summary>Where the host leaves what a Zing view dropped until the renderer collects it.</summary>
	public DropInbox Drops { get; }

	/// <summary>The files open in this window's viewer panels; null on a host with no temporary folder for converted copies.</summary>
	public ViewerFiles? Viewer { get; }

	public TabService Tabs { get; }

	/// <summary>Favourites and pinned items as saved tabs.</summary>
	public SavedTabs Saved { get; }

	public TabOrdering Ordering { get; }

	public PageUiStore PageUi { get; } = new();

	public FindController Find { get; }

	public DownloadList Downloads { get; }

	public PermissionBroker Permissions { get; }

	/// <summary>The vault's account picker and fill for this window's pages.</summary>
	public Autofill Autofill { get; }

	/// <summary>The save and update prompts for this window's pages.</summary>
	public SignInWatch SignIn { get; }

	public TabLifecycle Lifecycle { get; }

	/// <summary>Whether the reader relay and the archive may be asked this session.</summary>
	public RelayConsent Consent => _consent;

	public StackDropdown Stacks { get; }

	public ShellModel Shell { get; }

	public TabOverview Overview { get; }

	/// <summary>Fires after any change to this window's tabs, e.g. so the primary window's session can be saved.</summary>
	public event Action? TabsChanged;

	public ChromeApi(Bridge bridge, AppServices app, IWindowHost host, string appVersion)
	{
		_app = app;
		_consent = app.Consent;
		_host = host;
		_appVersion = appVersion;
		_placement = OutputPlacement.ForSettings(app.Settings);
		Tabs = new TabService(new TabStore(app.Now), host.Pages, BlankUrl, OnTabsChanged, LoadFailed, InterceptNavigation, app.SiteData, (_, text) => host.Notify(text), PeekSettings, host.Scheduler);
		Saved = new SavedTabs(Tabs, app.Library);
		Ordering = new TabOrdering(Tabs, app.Settings, host.Notify, host.Scheduler);
		Find = new FindController(PageUi, new FindHost(this));
		Downloads = new DownloadList(host.DownloadsDirectory, host.DownloadHost, PageUi, host.Notify);
		Permissions = new PermissionBroker(PageUi, HiddenTabOf);
		_secretCopy = new TimedSecretCopy(host.SecretClipboard, host.Scheduler, error => host.Report("clearing a copied password", error));
		SignIn = new SignInWatch(() => app.Vaults.Current, id => Tabs.IsShown(id), host.Scheduler, prompt => PageUi.Update(s => s with { SaveLogin = prompt }), host.Background);
		Autofill = new Autofill(() => app.Vaults.Current, id => Tabs.IsShown(id) ? Tabs.PageOf(id) : null, picker =>
		{
			host.Push("pageui:account-picker", picker);
			host.AccountPickerChanged(picker);
		}, app.Clock, host.Scheduler, host.Background, (pageId, origin, username, password) => SignIn.Filled(pageId, origin, username, password));
		Tabs.AutofillMessage += (pageId, source, message) =>
		{
			Autofill.Message(pageId, source, message);
			SignIn.Message(pageId, source, message);
		};
		Tabs.Closed += SignIn.PageClosed;
		Tabs.Navigating += SignIn.UserNavigated;
		Drops = new DropInbox(Grants);
		bridge.UseDropInbox(Drops);
		if (app.ViewerRoot is { } viewerRoot)
		{
			Viewer = new ViewerFiles(viewerRoot, _placement, (what, error) => host.Warn(what, error))
			{
				ModelOpener = () => OpenModel(app.Engines),
				SaveDirectory = host.DownloadsDirectory,
				IssueToken = path => Grants.Issue(path).Token,
			};
			bridge.UseViewer(Viewer);
		}
		Stacks = new StackDropdown(key => StackMembers.Of(Tabs.State(Setting("tabOrganization"), Flag("tabDropdowns")), Ordering.Stacks(), key), host.Scheduler, state =>
		{
			host.Push("stack:dropdown", state);
			host.StackDropdownChanged(state);
			// The strip redraws its pressed stack chip.
			host.Push("tabs:state", State());
		});
		Overview = new TabOverview(Tabs, State, state =>
		{
			host.Push("overlay:overview", state);
			host.OverviewChanged(state);
		}, (id, error) => host.Warn($"picture of tab {id}", error));
		Tabs.Leaving += Overview.Leaving;
		Tabs.Closed += Overview.Forget;
		Shell = new ShellModel(appVersion, new ShellWindow(PageArea, () => host.ContentSize, () => host.ContentOrigin, () => host.Cursor), host.Scheduler, Flag("sidebarPinned"), !app.Account.State().SignedIn, RecentFeatures());
		host.OfferEnded += EndOffer;
		Shell.StateChanged += state => host.Push("shell:state", state);
		Shell.PanelsChanged += state => host.Push("toolpanel:state", state);
		// A job belongs to its panel: once the panel is gone (minimized is not gone) its engine is stopped.
		Shell.PanelsChanged += state => app.Runner?.KillPanelsExcept(_windowId, state.Panels.Select(p => p.Id).ToHashSet(StringComparer.Ordinal));
		// A viewer file is shown in its panel and goes with it; its copy is deleted.
		Shell.PanelsChanged += state => Viewer?.CloseExcept(state.Panels.Select(p => p.Id).ToHashSet(StringComparer.Ordinal));
		Shell.SurfaceRegionChanged += host.SurfaceRegionChanged;
		Shell.FocusSurfaceRequested += host.FocusSurface;
		Shell.SetSidebarWidth(SidebarWidth());
		Shell.SetCollapsedWidth(CollapsedSidebar());
		Shell.FocusPageRequested += () =>
		{
			if (Tabs.ActiveId is { } id) Tabs.FocusPage(id);
		};
		Shell.RelayoutRequested += host.Relayout;
		Shell.NoticeRaised += host.Notify;
		Shell.RecentFeaturesChanged += recents => app.Settings.Set("recentFeatures", new JsonArray([.. recents.Select(r => (JsonNode)r)]));
		Shell.SidebarPinRequested += pinned => app.Settings.Set("sidebarPinned", pinned);
		_subscriptions.Add(app.Account.Subscribe(state =>
		{
			host.Push("account:state", state);
			Shell.AccountChanged(state.SignedIn);
		}));
		Lifecycle = new TabLifecycle(new LifecycleTabs(Tabs, Overview), app.Settings, app.Clock, host.Scheduler, error => host.Report("tab lifecycle", error));
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
		// The host places and hides the buttons' view with the preview's pages; this is only what they show.
		Tabs.PreviewChanged += () => host.Push("peek:state", Peek());
		// Never an offer: that would act on the tab under the preview, whose page is fine.
		Tabs.PreviewLoadFailed += (url, description) => host.Notify(CouldNotLoad(url, description));

		bridge.Register("tabs:get", _ => State());
		bridge.Register("tabs:activate", args => Do(() => Tabs.Activate(Text(args, 0))));
		bridge.Register("tabs:close", args => Do(() => Tabs.Close(Text(args, 0))));
		bridge.Register("tabs:navigate", args => Do(() => Navigate(Text(args, 0), Text(args, 1))));
		bridge.Register("tabs:open-link", args => Do(() => OpenLinkInNewTab(Text(args, 0))));
		bridge.Register("tabs:move", args => Do(() => Ordering.Move(Text(args, 0), Int(args, 1))));
		bridge.Register("tabs:move-stack", args => Do(() => Ordering.MoveStack(Text(args, 0), Int(args, 1))));
		bridge.Register("tabs:move-within", args => Do(() => Ordering.MoveWithin(Text(args, 0), Text(args, 1), Int(args, 2))));
		bridge.Register("tabs:set-organization", args => Do(() => Ordering.SetOrganization(TabOrganizations.Parse(Text(args, 0)))));
		bridge.Register("tabs:set-dropdowns", args => Do(() => Ordering.SetDropdowns(Bool(args, 0))));
		bridge.Register("stack:toggle", args => Do(() => Stacks.Toggle(Text(args, 0), Number(args, 1))));
		bridge.Register("stack:open-for-drag", args => Do(() => Stacks.OpenForDrag(Text(args, 0), Number(args, 1))));
		bridge.Register("stack:close", _ => Do(Stacks.Close));
		bridge.Register("stack:current", _ => Stacks.Current);
		bridge.Register("stack:drag-presence", args => Do(() => Stacks.DragPresence(Text(args, 0), Bool(args, 1))));
		bridge.Register("chrome:history-menu", args => HistoryMenuAsync(Text(args, 0), args));
		bridge.Register("chrome:strip-menu", args => Do(() => Menu(Menus.Strip(Ordering), args, 0)));
		bridge.Register("chrome:stack-menu", args => Do(() => Menu(Menus.Stack(Tabs, Saved, Ordering, Text(args, 0), host.Shell), args, 1)));
		bridge.Register("tabs:pin", args => Do(() => Saved.Pin(Text(args, 0))));
		bridge.Register("tabs:unpin", args => Do(() => Saved.Unpin(Text(args, 0))));
		bridge.Register("tabs:open-item", args => Do(() => Saved.Open(Text(args, 0))));
		bridge.Register("split:with", args => Do(() => Tabs.SplitWith(Text(args, 0), Text(args, 1))));
		bridge.Register("split:drop", args => Do(() => Tabs.SplitDrop(Text(args, 0), Text(args, 1), Text(args, 2))));
		bridge.Register("split:remove", args => Do(() => Tabs.Unsplit(Text(args, 0))));
		bridge.Register("split:drop-zone", args => Do(() => Shell.SetDropZone(Arg(args, 0) is { ValueKind: System.Text.Json.JsonValueKind.Object } zone ? new Rect(zone.GetProperty("x").GetDouble(), zone.GetProperty("y").GetDouble(), zone.GetProperty("width").GetDouble(), zone.GetProperty("height").GetDouble()) : null)));
		bridge.Register("split:ratios", args => Do(() => Tabs.SetSplitRatios(Text(args, 0), [.. Arg(args, 1).EnumerateArray().Select(r => r.GetDouble())], Arg(args, 2).GetDouble())));
		bridge.Register("tabs:save", args => Do(() => Saved.Save(Text(args, 0), Text(args, 1), Int(args, 2))));
		bridge.Register("tabs:close-others", args => Do(() => Tabs.CloseOthers(Text(args, 0))));
		bridge.Register("command:run", args => Run(Text(args, 0)));
		bridge.Register("settings:get", _ => app.Settings.Snapshot());
		bridge.Register("settings:set", args => Do(() => SetSetting(Text(args, 0), Arg(args, 1))));
		bridge.Register("library:get", _ => LibraryState());
		bridge.Register("library:apply", args => app.Library.Apply(LibraryStore.ParseLibraryAction(Arg(args, 0))));
		bridge.Register("library:open", args => Do(() => OpenLibraryItem(Text(args, 0), Text(args, 1))));
		bridge.Register("library:open-all", args => Do(() => OpenAll(Text(args, 0))));
		bridge.Register("library:panel", args => Do(() => SetLibraryPanel(OptionalText(args, 0))));
		bridge.Register("shell:get", _ => Shell.State());
		bridge.Register("shell:open-settings", args => Do(() => Shell.OpenSettings(OptionalText(args, 0))));
		bridge.Register("shell:close-settings", _ => Do(Shell.CloseSettings));
		bridge.Register("shell:toolbox", _ => Do(Shell.OpenToolbox));
		bridge.Register("shell:close-toolbox", _ => Do(Shell.CloseToolbox));
		bridge.Register("shell:clear-session", _ => ClearBrowsingSessionAsync());
		bridge.Register("shell:menu", args => Do(() => Menu([.. Shell.MenuItems(Text(args, 0), Text(args, 1)).Select(i => i.Invoke is null ? MenuItem.Separator : new MenuItem(i.Label, i.Invoke))], args, 2)));
		bridge.Register("toolpanel:get", _ => Shell.PanelsState());
		bridge.Register("toolpanel:open", args => Shell.OpenPanel(Text(args, 0), PointArg(args, 1)));
		bridge.Register("toolpanel:open-with-input", args => Shell.OpenPanelWithInput(Text(args, 0), Text(args, 1)));
		bridge.Register("toolpanel:close", args => Do(() => Shell.ClosePanel(Text(args, 0))));
		bridge.Register("toolpanel:minimize", args => Do(() => Shell.MinimizePanel(Text(args, 0))));
		bridge.Register("toolpanel:restore", args => Shell.RestorePanel(Text(args, 0)));
		bridge.Register("toolpanel:focus", args => Do(() => Shell.FocusPanel(Text(args, 0))));
		bridge.Register("toolpanel:begin-drag", args => Do(() => Shell.BeginPanelDrag(Text(args, 0))));
		bridge.Register("toolpanel:end-drag", args => Do(() => Shell.EndPanelDrag(Text(args, 0), PointArg(args, 1))));
		bridge.Register("overview:get", _ => Overview.Current);
		bridge.Register("overview:close", _ => Do(Overview.Close));
		bridge.Register("hash:text", args => Hashing.Text(Text(args, 0), Text(args, 1)));
		bridge.Register("hash:file", args => HashFileAsync(Text(args, 0), Text(args, 1)));
		bridge.Register("hash:hmac", args => Hashing.Hmac(Text(args, 0), Text(args, 1), Text(args, 2)));
		bridge.Register("engines:list", _ => Engines().List().Select(EngineInfo.Of).ToList());
		bridge.Register("engines:ensure", args => EnsureEngineAsync(Text(args, 0)));
		bridge.Register("engines:cancel", args => Do(() => CancelEngineDownload(Text(args, 0))));
		bridge.Register("engines:remove", args => Do(() => Engines().Remove(Text(args, 0))));
		bridge.Register("engines:update-ytdlp", _ => UpdateYtDlpAsync());
		bridge.Register("engines:run", args => RunEngine(Text(args, 0), Text(args, 1), Arg(args, 2)));
		// Not "engines:cancel": that one stops a download by engine id.
		bridge.Register("engines:cancel-job", args => Do(() => Runner().Cancel(Text(args, 0), _windowId)));
		bridge.Register("files:pick", args => PickFilesAsync(Arg(args, 0)));
		bridge.Register("convert:html-to-pdf", args => HtmlToPdfAsync(Text(args, 0), Text(args, 1)));
		bridge.Register("files:open-dropped", _ => Drops.Take());
		bridge.Register("files:copy-to-clipboard", args => Do(() => CopyFileToClipboard(Text(args, 0))));
		bridge.Register("session:open", args => OpenToolSession(Text(args, 0), Text(args, 1), StringArray(Arg(args, 2))));
		bridge.Register("session:open-save", args => OpenSaveSession(Text(args, 0), StringArray(Arg(args, 1))));
		bridge.Register("bgremove:open", args => OpenToolSession(Text(args, 0), Text(args, 1), ["image"]));
		bridge.Register("page:save", _ => SavePageAsync());
		bridge.Register("viewer:open", args => OpenViewerFile(Text(args, 0), Text(args, 1)));
		bridge.Register("viewer:close", args => Do(() => CloseViewerFile(Text(args, 0))));
		bridge.Register("net:diagnose", args => DiagnoseAsync(Text(args, 0)));
		bridge.Register("dns:preview", async _ => await DnsSetup().PreviewAsync(Setting("dohProvider")));
		bridge.Register("dns:apply", async args => await DnsSetup().ApplyAsync(Setting("dohProvider"), Text(args, 0)));
		bridge.Register("dns:undo", async args => await DnsSetup().UndoAsync(Text(args, 0)));
		bridge.Register("relay:open", args => OpenLinkAsync(Text(args, 0), Text(args, 1)));
		bridge.Register("relay:allow-third-parties", _ => Do(_consent.Allow));
		bridge.Register("tabs:open-relayed", args => Do(() => OpenRelayedTab(Text(args, 0))));
		bridge.Register("offer:choose", args => ChooseOffer(Text(args, 0)));
		bridge.Register("offer:dismiss", _ => Do(DismissOffer));
		bridge.Register("account:get", _ => app.Account.State());
		bridge.Register("account:sign-up", args =>
		{
			RequireSyncOff();
			return app.Account.Run(() => app.Account.SignUp(Text(args, 0), Text(args, 1), Text(args, 2), Text(args, 3)));
		});
		bridge.Register("account:sign-in", async args => await SignInToAccountAsync(Text(args, 0)));
		bridge.Register("account:reset-password", args => app.Account.Run(() => ResetAccountPassword(Text(args, 0))));
		bridge.Register("account:sign-out", _ => app.Account.Run(app.Account.SignOut));
		bridge.Register("account:continue-as-guest", _ => app.Account.Run(app.Account.ContinueAsGuest));
		bridge.Register("account:update-profile", args => app.Account.Run(() => app.Account.UpdateProfile(Text(args, 0), Text(args, 1))));
		bridge.Register("account:require-password", args => app.Account.Run(() => app.Account.SetRequirePasswordOnLaunch(Bool(args, 0))));
		bridge.Register("account:delete", _ =>
		{
			RequireSyncOff();
			return app.Account.Run(app.Account.DeleteAccount);
		});
		bridge.Register("account:change-password", async args => await ChangeAccountPasswordAsync(Text(args, 0), Text(args, 1)));
		bridge.Register("chrome:app-menu", args => Do(() => Menu(Menus.App(id => Run(id)), args, 0)));
		bridge.Register("chrome:tab-menu", args => Do(() => Menu(Menus.Tab(Tabs, Saved, Text(args, 0), host.Shell), args, 1)));
		bridge.Register("library:folder-menu", args => Do(() => Menu(Menus.LibraryFolder(app.Library, Text(args, 0), id => OpenLibraryItem(id, "current"), OpenAll), args, 1)));
		bridge.Register("library:item-menu", args => Do(() => Menu(Menus.LibraryItem(app.Library, Text(args, 0), OpenLibraryItem, OpenAll, Saved), args, 1)));
		bridge.Register("pageui:get", _ => PageUi.State);
		// A CSS height, fractional on a scaled display (52.5 at 150%); the view takes whole DIPs, rounded up.
		bridge.Register("pageui:height", args => Do(() => host.SetPageUiHeight((int)Math.Ceiling(Number(args, 0)))));
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

		bridge.Register("vault:get", _ => CurrentVaultState());
		bridge.Register("vault:summaries", _ => Vault().Summaries());
		bridge.Register("vault:reveal", async args => await Vault().PasswordAsync(Text(args, 0)));
		bridge.Register("vault:reveal-history", async args => await Vault().HistoryPasswordAsync(Text(args, 0), Int(args, 1)));
		bridge.Register("vault:save", async args => await Vault().SaveAsync(NewLoginArg(Arg(args, 0))));
		bridge.Register("vault:update", async args => await UpdateLoginAsync(Text(args, 0), Arg(args, 1)));
		bridge.Register("vault:delete", args => Do(() => Vault().Delete(Text(args, 0))));
		bridge.Register("vault:restore", args => Do(() => Vault().Restore(Text(args, 0))));
		bridge.Register("vault:history", args => Vault().History(Text(args, 0)));
		bridge.Register("vault:copy", async args => await CopyPasswordAsync(Text(args, 0)));
		bridge.Register("vault:import-csv", async args => await ImportCsvAsync(Text(args, 0)));
		bridge.Register("vault:export-csv", async _ => await ExportCsvAsync());
		bridge.Register("vault:never-saves", _ => Vault().NeverSaves());
		bridge.Register("vault:remove-never-save", args => Do(() => Vault().RemoveNeverSave(Text(args, 0))));
		bridge.Register("vault:fill", async args => await Autofill.FillAsync(Text(args, 0), Text(args, 1)));
		bridge.Register("vault:answer-save", async args => await SignIn.AnswerAsync(Text(args, 0), Text(args, 1)));
		bridge.Register("sync:get", _ => CurrentSyncState());
		bridge.Register("sync:sign-in", async _ => await SignInToSyncAsync());
		bridge.Register("sync:sign-out", async _ => await SignOutOfSyncAsync());
		bridge.Register("sync:cancel-sign-in", _ => Do(CancelSyncSignIn));
		bridge.Register("sync:turn-off", async _ => await TurnOffSyncAsync());
		bridge.Register("sync:turn-on", async args => new { recoveryKey = await TurnOnSyncAsync(Text(args, 0)) });
		bridge.Register("sync:confirm-recovery-saved", _ =>
		{
			Vault().ConfirmRecoverySaved();
			return CurrentSyncState();
		});
		bridge.Register("sync:show-recovery", async _ => new { recoveryKey = await Vault().RecoveryKeyAsync() });
		bridge.Register("sync:upload-local", async _ => await UploadLocalCopyAsync());
		bridge.Register("sync:restore-sign-in", async _ => await SignInToRestoreAsync());
		bridge.Register("sync:restore-password", async args => await RestoreWithPasswordAsync(Text(args, 0)));
		bridge.Register("sync:restore-recovery-key", async args =>
		{
			await RestoreWithRecoveryKeyAsync(Text(args, 0));
			return null;
		});
		bridge.Register("sync:restore-new-password", async args => await SetPasswordAfterRecoveryAsync(Text(args, 0)));
		bridge.Register("pageui:picker-get", _ => Autofill.Picker);
		bridge.Register("pageui:picker-height", args => Do(() => host.SetAccountPickerHeight((int)Math.Ceiling(Number(args, 0)))));
		bridge.Register("pageui:picker-close", _ => Do(Autofill.Dismiss));
		bridge.Register("peek:get", _ => Peek());
		_watchedVault = app.Vaults.Current;
		if (_watchedVault is { } vault) Watch(vault);
		app.Vaults.Changed += VaultSwitched;
		if (app.SyncClients is { } syncClients) syncClients.EngineStatusChanged += PushSyncState;

		_subscriptions.Add(app.Settings.Subscribe(OnSettingChanged));
		_unsubscribeLibrary = app.Library.Subscribe(_ => host.Push("library:state", LibraryState()));
	}

	public bool SettingsOpen => Shell.SettingsOpen;

	/// <summary>The window resized or the page moved: panels and the surface follow.</summary>
	public void LayoutChanged()
	{
		Shell.LayoutChanged();
		// The dividers and the drop zones the chrome works out follow the page area.
		_host.Push("tabs:state", State());
	}

	private Rect PageArea()
	{
		var insets = Insets();
		var size = _host.ContentSize;
		return new Rect(insets.Left, insets.Top, Math.Max(0, size.Width - insets.Left - insets.Right), Math.Max(0, size.Height - insets.Top));
	}

	private List<string> RecentFeatures() => [.. (_app.Settings.Get("recentFeatures")?.AsArray() ?? []).Select(n => n!.GetValue<string>())];

	public TabsState State() => Tabs.State(Setting("tabOrganization"), Flag("tabDropdowns")) with
	{
		OpenStack = Stacks?.OpenKey,
		Splits = Tabs.Splits.All,
		ShownSplit = Tabs.ActiveId is { } active && Tabs.Splits.Of(active) is { } split ? new ShownSplit(split.Id, Split.PaneRects(PageArea(), split)) : null,
		PageArea = PageArea(),
	};

	/// <summary>Where the chrome ends and the page begins. With tabs on the left the app sidebar folds into the rail.</summary>
	public ChromeInsets Insets()
	{
		var position = Setting("tabBarPosition");
		var pinned = Flag("sidebarPinned");
		// On the left the rail is the sidebar: Ctrl+S hides it like the app sidebar, and it slides out from the edge.
		var rail = position == "left" && !pinned ? 0 : Collapsed() ? Layout.RailCollapsedWidth : RailWidth();
		var sidebar = position != "left" && pinned ? Layout.SidebarColumnWidth : 0;
		var panel = _libraryPanel is null ? 0 : Layout.LibraryPanelWidth;
		return Layout.Insets(position, FavouritesBarShown(), rail, sidebar + panel);
	}

	/// <summary>The Favourites bar shows with tabs on top only; on the side, the rail's tiles are the Favourites.</summary>
	private bool FavouritesBarShown() => Flag("showFavouritesBar") && Setting("tabBarPosition") == "top";

	/// <summary>The rail is a column of icons (tabs on the side).</summary>
	private bool Collapsed() => Flag("verticalTabsCollapsed") && Setting("tabBarPosition") != "top";

	/// <summary>On the left a collapsed rail keeps its icon column, and the full rail slides out over the page from it.</summary>
	private int? CollapsedSidebar() => Collapsed() && Setting("tabBarPosition") == "left" ? Layout.RailCollapsedWidth : null;

	private int RailWidth() => _app.Settings.Get("verticalTabWidth")?.GetValue<int>() ?? throw new InvalidDataException("Setting verticalTabWidth is missing");

	/// <summary>The left sidebar: the tab rail when tabs are on the left, the app sidebar's column otherwise.</summary>
	private int SidebarWidth() => Setting("tabBarPosition") == "left" ? RailWidth() : Layout.SidebarColumnWidth;

	/// <summary>Opens the first tab, or brings back a saved session.</summary>
	public void Start(IReadOnlyList<SessionTab> restored, string? startUrl)
	{
		if (restored.Count > 0)
		{
			Tabs.Restore(restored);
			Saved.AdoptPinnedTabs();
		}
		else Tabs.Open(startUrl ?? HomepageUrl());
		Shell.Start();
	}

	/// <summary>A link from another app: a new tab, or the lone blank tab reused. Non-web links are refused.</summary>
	public void OpenLink(string url)
	{
		AssertNavigable(url);
		if (Tabs.Records is [var only] && (only.Url == TabStore.NewTabUrl || StartPage.IsStartPage(only.Url))) Navigate(only.Id, url);
		else Tabs.Open(LinkCleaner.StripTrackers(url));
	}

	/// <summary>A link tile: one new tab at the address, so there is no blank page behind it in Back and the keyboard stays with the page.</summary>
	private void OpenLinkInNewTab(string url)
	{
		AssertNavigable(url);
		var opened = Tabs.Open(url);
		Tabs.FocusPage(opened.Id);
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

	/// <summary>
	/// A key pressed in a link preview's page or its buttons. Esc closes the preview's top layer, unless that page is in
	/// HTML fullscreen or shows a JavaScript dialog: then it is the page's, to leave fullscreen or close the dialog. A held Esc closes one layer: its repeats are taken and do
	/// nothing. Every other key is the window's, as anywhere else.
	/// </summary>
	public bool HandlePreviewKey(KeyInput input)
	{
		if (input is not { Code: "Escape", Ctrl: false, Shift: false, Alt: false } || Tabs.Preview is not { } preview) return HandleKey(input);
		if (preview.Top.Page.IsFullScreen || preview.Top.Page.HasOpenDialog) return false;
		if (input.Repeat) return true;
		return Run("preview.close") is not Outcome { Status: "not-applicable" };
	}

	public object Run(string command)
	{
		// Behind the account gate nothing may act on the browser.
		if (Shell.Gated) return NotApplicable;
		var outcome = RunCommand(command);
		if (outcome is NotImplemented missing) _host.Notify($"{missing.Label} isn't implemented yet");
		return outcome;
	}

	public void Dispose()
	{
		_disposed = true;
		_app.Runner?.KillWindow(_windowId);
		Viewer?.Dispose();
		_closing.Cancel();
		_closing.Dispose();
		_host.OfferEnded -= EndOffer;
		foreach (var subscription in _subscriptions) subscription.Dispose();
		_app.Vaults.Changed -= VaultSwitched;
		if (_app.SyncClients is { } syncClients) syncClients.EngineStatusChanged -= PushSyncState;
		if (_watchedVault is { } vault) Unwatch(vault);
		CancelSyncSignIn();
		Interlocked.Exchange(ref _restore, null)?.Dispose();
		Autofill.Dispose();
		SignIn.Dispose();
		_secretCopy.Dispose();
		Shell.Dispose();
		Ordering.Dispose();
		Lifecycle.Dispose();
		_unsubscribeLibrary();
		Saved.Dispose();
		Tabs.Destroy();
	}

	/// <summary>What a link preview's buttons show (src/shared/browser/peek.ts).</summary>
	public sealed record PeekState(string Host, bool CanExpand, bool HighContrast);

	/// <summary>The open preview's buttons, or null when there is none.</summary>
	private PeekState? Peek() =>
		Tabs.Preview is { } preview ? new PeekState(HostOf(preview.Top.Address), preview.CanExpand, _host.HighContrast) : null;

	/// <summary>The site a preview shows (a relayed page by the site it stands for); an address with no host, such as about:blank, as it is.</summary>
	private static string HostOf(string url)
	{
		var web = Net.RelayedContent.ToWebAddress(url);
		return Uri.TryCreate(web, UriKind.Absolute, out var uri) && uri.Host != "" ? uri.Host : web;
	}

	private sealed record Outcome(string Status);
	private sealed record NotImplemented(string Label) { public string Status => "not-implemented"; }

	private static readonly Outcome Done = new("done");
	private static readonly Outcome NotApplicable = new("not-applicable");

	private object RunCommand(string command)
	{
		// The page in front: an open preview's top page, never the tab hidden under it.
		var page = Tabs.Preview?.Top.Page ?? Tabs.ActivePage;
		// Find works on the active tab only (FindController), so it has nothing to search while a preview covers it.
		var findable = Tabs.Preview is null && page is not null;
		if (command.StartsWith("tab.select", StringComparison.Ordinal) && int.TryParse(command["tab.select".Length..], out var n)) return ActivateIfAny(Tabs.IdForShortcut(n));
		return command switch
		{
			"app.settings" => Act(true, () => Shell.OpenSettings()),
			"view.sidebar" => Act(true, Shell.ToggleSidebar),
			"view.favouritesBar" => Act(true, () => _app.Settings.Set("showFavouritesBar", !Flag("showFavouritesBar"))),
			"library.addFavourite" => SaveActive(favourite: true),
			"library.addReadingList" => SaveActive(favourite: false),
			"library.bookmarks" => Act(true, () => SetLibraryPanel(_libraryPanel == "bookmarks" ? null : "bookmarks")),
			"library.readingList" => Act(true, () => SetLibraryPanel(_libraryPanel == "readingList" ? null : "readingList")),
			"tab.new" => Act(true, NewTab),
			"tab.close" => Tabs.Preview is not null ? Act(true, Tabs.ClosePreview) : Act(Tabs.ActiveId is not null, () => Tabs.Close(Tabs.ActiveId!)),
			"tab.reopenClosed" => Tabs.ReopenClosed() is null ? NotApplicable : Done,
			"tab.split" => Act(Tabs.ActiveId is not null, SplitWithNewTab),
			"tab.next" => ActivateIfAny(Tabs.NextId()),
			"tab.previous" => ActivateIfAny(Tabs.PreviousId()),
			"window.new" => Act(true, _host.NewWindow),
			"window.close" => Act(true, _host.CloseWindow),
			"location.open" => Act(true, FocusAddress),
			"history.back" => Act(page is { CanGoBack: true }, () => page!.GoBack()),
			"history.forward" => Act(page is { CanGoForward: true }, () => page!.GoForward()),
			"history.home" => Act(Tabs.ActiveId is not null, () => Tabs.Navigate(Tabs.ActiveId!, HomepageUrl())),
			"page.reload" => Act(page is not null, () => page!.Reload()),
			"page.find" => Act(findable, Find.Open),
			"page.print" => Act(page is not null, () => page!.Print()),
			"page.reader" => Act(page is not null, () => ToggleReader(page!)),
			"tabs.overview" => Act(true, () => _host.Background(Overview.ToggleAsync(), "tab overview")),
			"page.textBigger" => Act(page is not null, () => page!.TextSize = TextSize.Bigger(page.TextSize)),
			"page.textSmaller" => Act(page is not null, () => page!.TextSize = TextSize.Smaller(page.TextSize)),
			"page.zoomIn" => Act(page is not null, () => page!.Zoom = Zoom.In(page.Zoom)),
			"page.zoomOut" => Act(page is not null, () => page!.Zoom = Zoom.Out(page.Zoom)),
			"page.actualSize" => Act(page is not null, () => page!.Zoom = Zoom.Default),
			"page.findNext" => Act(findable, () => Find.Step(FindDirection.Next)),
			"page.findPrevious" => Act(findable, () => Find.Step(FindDirection.Previous)),
			"page.stop" => Act(page is { IsLoading: true }, () => page!.Stop()),
			"preview.close" => Act(Tabs.Preview is not null, Tabs.CloseTopLayer),
			"preview.expand" => Act(Tabs.Preview is { CanExpand: true }, () => Tabs.ExpandPreview()),
			_ => new NotImplemented(Commands.Get(command).Label),
		};
	}

	/// <summary>Leaving reader mode reloads the page, which brings the original document back.</summary>
	private void ToggleReader(IPage page)
	{
		if (page.InReader)
		{
			page.Reload();
			return;
		}
		_host.Background(ShowReaderAsync(page), "reader");
	}

	private async Task ShowReaderAsync(IPage page)
	{
		if (await page.ShowReaderAsync() is { } reason) _host.Notify(reason);
	}

	/// <summary>
	/// Settings' "Clear browsing session": signs out of everything now, as
	/// Electron's session.clearStorageData + clearCache + clearAuthCache did.
	/// Tabs, the library and settings stay; only what sites stored goes.
	/// </summary>
	private async Task<object?> ClearBrowsingSessionAsync()
	{
		var data = _app.BrowsingData ?? throw new InvalidOperationException("This host can't clear the browsing session");
		await data.ClearAllAsync();
		_host.Notify("Browsing session cleared");
		return null;
	}

	/// <summary>
	/// Writes the active tab's HTML to Downloads. Only a Zing view can ask: a page view has no handler on the bridge. The
	/// direct route, by script; a relay route would fetch the text elsewhere and call <see cref="PageSave.Write"/> the same way.
	/// </summary>
	private async Task<object?> SavePageAsync()
	{
		var page = Tabs.ActivePage ?? throw new PageNotSavableException("There is no page open to save.");
		var text = await PageSave.ReadAsync(page);
		return Grants.Issue(PageSave.Write(_placement, _host.DownloadsDirectory, text.Title, text.Html));
	}

	private EngineStore Engines() => _app.Engines ?? throw new InvalidOperationException("This host has no engine store");

	private EngineRunner Runner() => _app.Runner ?? throw new InvalidOperationException("This host can't run engines");

	private void CopyFileToClipboard(string token)
	{
		var path = Grants.Resolve(token);
		if (!File.Exists(path)) throw new FileNotFoundException($"{Path.GetFileName(path)} is no longer there, so it can't be copied", path);
		_host.CopyFilesToClipboard([path]);
	}

	private async Task<object?> PickFilesAsync(JsonElement kinds)
	{
		if (kinds.ValueKind != JsonValueKind.Array) throw new ArgumentException("Argument 0 must be a list of file kinds");
		var extensions = FileKinds.Extensions([.. kinds.EnumerateArray().Select(k => k.ValueKind == JsonValueKind.String ? k.GetString()! : throw new ArgumentException("A file kind must be a string"))]);
		var paths = await _host.PickFilesAsync(extensions);
		return paths.Select(Grants.Issue).ToList();
	}

	/// <summary>The model and a hold on it, so the store will not remove it while the host streams it.</summary>
	private static (string Path, IDisposable Lease) OpenModel(EngineStore? engines)
	{
		var store = engines ?? throw new EngineNotInstalledException(EngineIds.BackgroundModel);
		var lease = store.Use(EngineIds.BackgroundModel);
		try
		{
			return (store.InstalledPath(EngineIds.BackgroundModel), lease);
		}
		catch
		{
			lease.Dispose();
			throw;
		}
	}

	private static List<string> StringArray(JsonElement list) =>
		list.ValueKind == JsonValueKind.Array ? [.. list.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString()! : throw new ArgumentException("Kinds must be a list of names"))] : throw new ArgumentException("Argument 2 must be a list of kinds");

	/// <summary>
	/// Opens a picked or dropped file for a tool panel as a read session on the viewer file server. The host judges the first bytes
	/// against the kinds asked for (a fixed table: image, pdf) and the length cap, and answers with a session address; the token and
	/// the path never reach the tool. Closing the panel, opening another file in it or <c>viewer:close</c> revokes it.
	/// </summary>
	private object OpenToolSession(string panelId, string token, IReadOnlyList<string> kindNames)
	{
		var viewer = Viewer ?? throw new InvalidOperationException("This host can't serve files to tools");
		var kinds = SessionKinds.Named(kindNames);
		var path = Grants.Resolve(token);
		if (!File.Exists(path)) throw new FileNotFoundException($"{Path.GetFileName(path)} isn't there any more. Pick it again", path);
		if (!Shell.PanelsState().Panels.Any(p => p.Id == panelId)) throw new UnknownPanelException(panelId);
		// Judged before anything is closed: a refused file leaves the panel's live session as it was.
		ViewerFiles.Judge(path, kinds);
		CloseViewerPanel(panelId);
		return Opened(viewer.OpenSession(path, panelId, kinds));
	}

	/// <summary>A session that takes results only (see <see cref="ViewerFiles.OpenSaveSession"/>), for a tool that has read its file and must not depend on it any more.</summary>
	private object OpenSaveSession(string panelId, IReadOnlyList<string> kindNames)
	{
		var viewer = Viewer ?? throw new InvalidOperationException("This host can't serve files to tools");
		var kinds = SessionKinds.Named(kindNames);
		if (!Shell.PanelsState().Panels.Any(p => p.Id == panelId)) throw new UnknownPanelException(panelId);
		CloseViewerPanel(panelId);
		return Opened(viewer.OpenSaveSession(panelId, kinds));
	}

	/// <summary>Convert's HTML to PDF: prints the file a token stands for and answers with the PDF's name and a token.</summary>
	private async Task<object?> HtmlToPdfAsync(string token, string outputName)
	{
		var path = Grants.Resolve(token);
		var placed = await HtmlPdf.PrintAsync(path, outputName, _host.DownloadsDirectory, _placement, _host.PrintHtmlToPdfAsync, _closing.Token);
		return Grants.Issue(placed);
	}

	/// <summary>
	/// <c>spec</c> is <c>{ args: (string | {input: token} | {output: n})[], outputs?: string[], urls?: string[] }</c>.
	/// Files come only as tokens this window's picker or a drop issued, so the renderer can't name another path.
	/// </summary>
	private object RunEngine(string engineId, string panelId, JsonElement spec)
	{
		var runner = Runner();
		if (!Shell.PanelsState().Panels.Any(p => p.Id == panelId)) throw new UnknownPanelException(panelId);
		if (spec.ValueKind != JsonValueKind.Object || !spec.TryGetProperty("args", out var args) || args.ValueKind != JsonValueKind.Array) throw new ArgumentException("The job needs a list of args");
		var request = new EngineJobRequest(
			engineId,
			[.. args.EnumerateArray().Select(JobArgument)],
			_host.DownloadsDirectory,
			Strings(spec, "outputs"),
			Strings(spec, "urls"));
		return runner.Start(new JobOwner(_windowId, panelId), request, PushJobOutput, PushJobExit);
	}

	/// <summary>
	/// Shows a picked or dropped file in a viewer panel. Chromium's own formats are served from where they are; the rest are converted
	/// into a temporary copy by an engine owned by the panel, so closing the panel stops the job and deletes the copy.
	/// </summary>
	private object OpenViewerFile(string token, string panelId)
	{
		var viewer = Viewer ?? throw new InvalidOperationException("This host can't show files");
		var path = Grants.Resolve(token);
		if (!File.Exists(path)) throw new FileNotFoundException($"{Path.GetFileName(path)} isn't there any more. Pick it again", path);
		if (!Shell.PanelsState().Panels.Any(p => p.Id == panelId)) throw new UnknownPanelException(panelId);
		var format = ViewerFormats.For(Path.GetExtension(path)) ?? throw new UnsupportedViewerFormatException(Path.GetExtension(path).TrimStart('.'));
		CloseViewerPanel(panelId);
		if (!format.Converted) return Opened(viewer.OpenNative(path, panelId, format));
		var runner = Runner();
		var session = viewer.OpenConverted(path, panelId, format);
		try
		{
			var request = new EngineJobRequest(
				ViewerConversion.EngineFor(format.Kind),
				ViewerConversion.Args(format.Kind, path, Path.GetExtension(path)),
				session.Folder!,
				[session.OutputName!],
				[],
				staged => viewer.BeginLive(session.Id, staged[0]));
			session.JobId = runner.Start(new JobOwner(_windowId, panelId), request, PushJobOutput, exit => EndViewerJob(viewer, session, exit));
		}
		catch
		{
			viewer.Abandon(session.Id);
			throw;
		}
		return Opened(session);
	}

	private sealed record ViewerOpened(string Id, string Name, string Show, string Url, string? JobId, bool Stream);

	private static ViewerOpened Opened(ViewerSession session) => new(session.Id, session.Name, session.Format.Show, session.Url, session.JobId, session.Stream);

	/// <summary>The copy is done, or it isn't. The renderer is told by the usual exit event, without a token for the temporary file.</summary>
	private void EndViewerJob(ViewerFiles viewer, ViewerSession session, JobExit exit)
	{
		if (exit.Code == 0 && !exit.Cancelled && exit.Error is null && exit.Outputs.Count == 1) viewer.Complete(session.Id, exit.Outputs[0]);
		else viewer.Fail(session.Id);
		if (!_disposed) _host.Push("engines:exit", new JobExitEvent(exit.JobId, exit.Code, exit.Cancelled, [], exit.StderrTail, exit.Error));
	}

	private void CloseViewerFile(string id)
	{
		if (Viewer?.Find(id) is { } session) CloseViewerSession(session);
	}

	private void CloseViewerPanel(string panelId)
	{
		if (Viewer?.ForPanel(panelId) is { } session) CloseViewerSession(session);
	}

	private void CloseViewerSession(ViewerSession session)
	{
		if (session.JobId is { } job) StopJob(job);
		Viewer!.Close(session.Id);
	}

	private void StopJob(string jobId)
	{
		try
		{
			Runner().Cancel(jobId, _windowId);
		}
		catch (UnknownJobException)
		{
			// So long ago that the runner has forgotten it: it is not running.
		}
	}

	private JobArg JobArgument(JsonElement arg)
	{
		if (arg.ValueKind == JsonValueKind.String) return new JobArg.Literal(arg.GetString()!);
		if (arg.ValueKind == JsonValueKind.Object && arg.TryGetProperty("input", out var token) && token.ValueKind == JsonValueKind.String)
		{
			var path = Grants.Resolve(token.GetString()!);
			return File.Exists(path) ? new JobArg.Input(path) : throw new FileNotFoundException($"{Path.GetFileName(path)} isn't there any more. Pick it again", path);
		}
		if (arg.ValueKind == JsonValueKind.Object && arg.TryGetProperty("output", out var index) && index.TryGetInt32(out var number)) return new JobArg.Output(number);
		throw new ArgumentException("A job argument is a string, {input: token} or {output: number}");
	}

	private static List<string> Strings(JsonElement spec, string name)
	{
		if (!spec.TryGetProperty(name, out var list) || list.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return [];
		if (list.ValueKind != JsonValueKind.Array) throw new ArgumentException($"{name} must be a list of strings");
		return [.. list.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString()! : throw new ArgumentException($"{name} must be a list of strings"))];
	}

	// A closed window has no renderer to tell.
	private void PushJobOutput(JobOutputBatch batch)
	{
		if (!_disposed) _host.Push("engines:output", batch);
	}

	/// <summary>
	/// What the renderer is told when a job ends. Each output is a name and a token (to show, and to give a later job as an
	/// input), not a path. The engine's own lines and <c>StderrTail</c> can still contain paths: FFmpeg prints the files it
	/// reads, and a tool needs those lines to show progress and errors.
	/// </summary>
	private sealed record JobExitEvent(string JobId, int Code, bool Cancelled, IReadOnlyList<FileGrant> Outputs, IReadOnlyList<string> StderrTail, string? Error);

	private void PushJobExit(JobExit exit)
	{
		if (_disposed) return;
		_host.Push("engines:exit", new JobExitEvent(exit.JobId, exit.Code, exit.Cancelled, [.. exit.Outputs.Select(Grants.Issue)], exit.StderrTail, exit.Error));
	}

	/// <summary>A person asked for the download (a button, after being told its size and source); progress reaches this window.</summary>
	private async Task<object?> EnsureEngineAsync(string id)
	{
		var progress = new PercentProgress(fraction => _host.Push("engines:progress", new { id, fraction }));
		using var own = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
		if (!_downloads.TryAdd(id, own)) throw new InvalidOperationException($"{id} is already downloading in this window");
		try
		{
			await Engines().EnsureAsync(id, progress, own.Token);
			return "installed";
		}
		// A cancel is an ordinary answer, not an error; EnsureAsync has removed everything it left before it throws.
		catch (OperationCanceledException) when (own.IsCancellationRequested)
		{
			return "cancelled";
		}
		finally
		{
			_downloads.Remove(id);
		}
	}

	private void CancelEngineDownload(string id)
	{
		if (_downloads.TryGetValue(id, out var download)) download.Cancel();
	}

	private async Task<object?> UpdateYtDlpAsync() => await Engines().UpdateYtDlpAsync(_closing.Token);

	/// <summary>A file the person picked, by its token: a typed path could name a share, and opening one sends their Windows sign-in hash.</summary>
	private async Task<object?> HashFileAsync(string token, string algorithm) => await Hashing.FileAsync(Grants.Resolve(token), algorithm);

	private readonly TimedSecretCopy _secretCopy;

	private VaultStore Vault() => _app.Vaults.Current ?? throw new VaultOffException();

	private VaultState CurrentVaultState() => _app.Vaults.Current is { } vault
		? new(true, vault.HelloAvailable, vault.Summaries().Count(s => s.DeletedAt is null), null, _app.Vaults.FilesExist)
		: new(false, false, 0, _app.Vaults.Unreadable ? "unreadable" : null, _app.Vaults.FilesExist);

	/// <summary>The store vault:state follows; it changes when passwordAutofill is turned on or off.</summary>
	private VaultStore? _watchedVault;

	/// <summary>passwordAutofill was turned on or off: follow the new store, and a picker on screen goes (Ruling 30).</summary>
	private void VaultSwitched()
	{
		if (_watchedVault is { } old) Unwatch(old);
		_watchedVault = _app.Vaults.Current;
		if (_watchedVault is { } now) Watch(now);
		Autofill.Close();
		SignIn.Clear();
		PushVaultState();
		PushSyncState();
	}

	private void Watch(VaultStore vault)
	{
		vault.Changed += PushVaultState;
		vault.SyncChanged += PushSyncState;
		vault.SyncPasswordChangedElsewhere += PushSyncState;
	}

	private void Unwatch(VaultStore vault)
	{
		vault.Changed -= PushVaultState;
		vault.SyncChanged -= PushSyncState;
		vault.SyncPasswordChangedElsewhere -= PushSyncState;
	}

	private void PushSyncState() => _host.Push("sync:state", CurrentSyncState());

	private void PushVaultState() => _host.Push("vault:state", CurrentVaultState());

	/// <summary>With a password, one store change that asks Hello at most once; a cancelled prompt changes nothing. Without one, no prompt.</summary>
	private async Task<object?> UpdateLoginAsync(string id, JsonElement edit)
	{
		var vault = Vault();
		var username = RequiredText(edit, "username");
		var title = RequiredText(edit, "title");
		var note = RequiredText(edit, "note");
		if (!edit.TryGetProperty("password", out var password) || password.ValueKind == JsonValueKind.Null)
		{
			vault.UpdateSummary(id, title, note, username);
			return null;
		}
		if (password.ValueKind != JsonValueKind.String) throw new ArgumentException("A login's password must be a string");
		await vault.UpdateLoginAsync(id, title, note, username, password.GetString()!);
		return null;
	}

	/// <summary>Asks Hello, then copies the password from the host: it never reaches the renderer, and it is cleared 30 s later if still the clipboard's content.</summary>
	private async Task<object?> CopyPasswordAsync(string id)
	{
		var password = await Vault().PasswordAsync(id);
		_secretCopy.Copy(password);
		return null;
	}

	/// <summary>Reads the picked CSV (a token, never a path) and imports it with one Hello call; rows that can't be used come back with the reason.</summary>
	private async Task<object?> ImportCsvAsync(string token)
	{
		var vault = Vault();
		var csv = VaultCsv.ParsePasswords(VaultCsv.ReadFile(Grants.Resolve(token)));
		var outcome = await vault.ImportAsync([.. csv.Logins.Select(l => new NewLogin(l.Url, l.Username, l.Password, l.Title, l.Note))]);
		return new { outcome.Added, outcome.Merged, csv.Skipped };
	}

	/// <summary>One Hello call, then every password in Chrome's CSV format, saved into Downloads. The renderer is told the file's name and gets a token for it.</summary>
	private async Task<object?> ExportCsvAsync()
	{
		var logins = await Vault().ExportAsync();
		return Grants.Issue(VaultCsv.SaveExport(_placement, _host.DownloadsDirectory, logins));
	}

	/// <summary>
	/// Signed out while the vault is off. A saved sign-in that doesn't unseal is reported, so Settings can offer Sign in again
	/// (Ruling 50). A recovery key still to confirm is reported whoever is signed in, or no one.
	/// </summary>
	private SyncState CurrentSyncState()
	{
		if (_app.Vaults.Current is not { } vault) return SyncState.SignedOut();
		try
		{
			if (vault.SyncAccount() is { } account) return SignedInState(vault, account);
		}
		catch (SyncAccountUnreadableException)
		{
			return SyncState.SignedOut("unreadable", vault);
		}
		return SyncState.SignedOut(vault.SyncSignedOutByServer ? "signed-out" : null, vault);
	}

	/// <summary>
	/// Sync is on only for the account this PC turned it on for: keys.bin names that server user. The engine's last error shows
	/// whenever signed in, so one met settling a turn-on still pending shows too (M7); how many logins wait to upload, only while on.
	/// </summary>
	private SyncState SignedInState(VaultStore vault, SyncAccount account)
	{
		var state = SyncState.SignedInAs(account, vault);
		if (_app.SyncClients?.Engine is not { } engine) return state;
		var status = engine.Status;
		return state with { Error = status.Error, Message = status.Message, Unsent = state.On ? status.Unsent : 0 };
	}

	/// <summary>
	/// Upload this PC's copy (spec, Rollback protection): what Settings offers after the server went back or held a login back, and
	/// only then (the engine refuses it otherwise, Ruling 56).
	/// </summary>
	private async Task<SyncState> UploadLocalCopyAsync()
	{
		var engine = _app.SyncClients?.Engine ?? throw new SyncNotSetUpException();
		await engine.UploadLocalCopyAsync(_closing.Token);
		return CurrentSyncState();
	}

	/// <summary>The sign-in waiting on the browser, for Cancel; null when none is.</summary>
	private CancellationTokenSource? _syncSigningIn;

	/// <summary>
	/// Google in the system browser, then the server's own session; the account is kept in the vault's sync.json, replacing one that
	/// doesn't unseal. Cancel (or the window closing) ends it without an error. Keys and items sync later (Tasks 12 and 13).
	/// </summary>
	private async Task<object?> SignInToSyncAsync()
	{
		var setup = _app.Sync ?? throw new SyncNotSetUpException();
		var vault = Vault();
		return await WithGoogleSignInAsync(setup, async (idToken, cancellation) => SignedInState(vault, await SyncClient(vault).SignInAsync(idToken, cancellation)), CurrentSyncState);
	}

	/// <summary>
	/// Google in the system browser, then <paramref name="signIn"/> with its ID token, within 5 minutes. Cancel (or the window
	/// closing) answers <paramref name="cancelled"/> without an error.
	/// </summary>
	private async Task<object?> WithGoogleSignInAsync(SyncSetup setup, Func<string, CancellationToken, Task<object?>> signIn, Func<object?> cancelled)
	{
		using var cancel = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
		using var signingIn = CancellationTokenSource.CreateLinkedTokenSource(cancel.Token);
		signingIn.CancelAfter(SyncSignInTimeout);
		Interlocked.Exchange(ref _syncSigningIn, cancel)?.Cancel();
		try
		{
			return await signIn(await setup.SignIn.SignInAsync(signingIn.Token), signingIn.Token);
		}
		catch (OperationCanceledException) when (cancel.IsCancellationRequested)
		{
			return cancelled();
		}
		catch (OperationCanceledException error) when (signingIn.IsCancellationRequested)
		{
			throw new SyncSignInTimeoutException(error);
		}
		finally
		{
			Interlocked.CompareExchange(ref _syncSigningIn, null, cancel);
		}
	}

	private void CancelSyncSignIn() => _syncSigningIn?.Cancel();

	/// <summary>
	/// Revokes this sign-in on the server, then forgets it here (Ruling 49). An unreachable server still signs this PC out, and the
	/// answer says the server's copy expires within 90 days. A saved sign-in that doesn't unseal has nothing to revoke and is forgotten.
	/// </summary>
	private async Task<object?> SignOutOfSyncAsync()
	{
		var vault = Vault();
		var setup = _app.Sync;
		try
		{
			if (vault.SyncAccount() is null) return CurrentSyncState();
		}
		catch (SyncAccountUnreadableException)
		{
			vault.ForgetSyncAccount();
			return SyncState.SignedOut(null, vault);
		}
		if (setup is null)
		{
			vault.ForgetSyncAccount();
			return SyncState.SignedOut("kept-on-server", vault);
		}
		var revoked = await SyncClient(vault).SignOutAsync(_closing.Token);
		return SyncState.SignedOut(revoked ? null : "kept-on-server", vault);
	}

	/// <summary>Turn off sync on this PC (Ruling 68): signs out, best effort, and forgets every sync record here; the logins stay.</summary>
	private async Task<SyncState> TurnOffSyncAsync()
	{
		var vault = Vault();
		var revoked = await SyncKeys.TurnOffAsync(vault, _app.SyncClients?.For(vault), _closing.Token);
		return SyncState.SignedOut(revoked ? null : "kept-on-server", vault);
	}

	private SyncClient SyncClient(VaultStore vault) => (_app.SyncClients ?? throw new SyncNotSetUpException()).For(vault);

	/// <summary>Turns sync on for the signed-in Google account (SyncKeys.TurnOnAsync) and answers the recovery key, the one time it is given without Hello.</summary>
	private async Task<string> TurnOnSyncAsync(string password)
	{
		var vault = Vault();
		return await new SyncKeys(_app.Account, vault, SyncClient(vault)).TurnOnAsync(password, _closing.Token);
	}

	/// <summary>
	/// A broken account rule is a 'rejected' outcome, as the other account channels give. With sync on (keys.bin says so), the keys
	/// are re-wrapped and uploaded first, and a weak password or keys changed on another PC raise. With the vault open, every change
	/// goes through SyncKeys, synced or not, so it runs under the vault's key lock and can't slip past a turn-on in another window
	/// (Ruling 55). While the vault is off, a keys.bin that says sync is on refuses the change rather than leave the server's keys
	/// wrapped by the old password.
	/// </summary>
	private async Task<object?> ChangeAccountPasswordAsync(string current, string next)
	{
		if (_app.Vaults.Current is null && _app.Vaults.SyncOnDisk) throw new SyncNeedsTheVaultException();
		try
		{
			if (_app.Vaults.Current is { } vault && _app.SyncClients is { } clients)
				await new SyncKeys(_app.Account, vault, clients.For(vault)).ChangePasswordAsync(current, next, _closing.Token);
			else if (_app.Vaults.SyncOnDisk) throw new SyncNotSetUpException();
			else _app.Account.ChangePassword(current, next);
		}
		catch (AccountException error)
		{
			return AccountOutcome.Rejected(error.Code);
		}
		return AccountOutcome.Ok(_app.Account.State());
	}

	/// <summary>
	/// Deleting the account, or making one, with sync on (keys.bin says so) would leave the server's keys wrapped by a password this
	/// PC no longer asks for (final review I2), so both are refused. Nothing signs out of sync on its own.
	/// </summary>
	private void RequireSyncOff()
	{
		if (!_app.Vaults.SyncOnDisk) return;
		if (_app.Vaults.Unreadable) throw new SyncIsOnWithTheVaultUnreadableException();
		if (_app.Vaults.Current is null) throw new SyncIsOnWithTheVaultOffException();
		throw new SyncIsOnException();
	}

	/// <summary>
	/// A forgotten password's reset sets a new local check only, so with sync on it's refused (Ruling 54): the gate sends the person to
	/// Restore from sync with the recovery key instead, which sets the new password on the server and here (Task 14).
	/// </summary>
	private void ResetAccountPassword(string password)
	{
		if (_app.Vaults.SyncOnDisk) throw new SyncNeedsRecoveryKeyException();
		_app.Account.ResetPassword(password);
	}

	/// <summary>
	/// The gate's Sign In. After sync found the password changed on another PC, the password typed is checked against the newer synced
	/// keys, not this PC's old check (Task 14). A broken account rule is a 'rejected' outcome, as the other account channels give.
	/// </summary>
	private async Task<AccountOutcome> SignInToAccountAsync(string password)
	{
		try
		{
			if (_app.Account.State().PasswordChangedElsewhere) await SyncRestore.SignInWithPasswordChangedElsewhereAsync(_app.Account, Vault(), password, _closing.Token);
			else _app.Account.SignIn(password);
		}
		catch (AccountException error)
		{
			return AccountOutcome.Rejected(error.Code);
		}
		return AccountOutcome.Ok(_app.Account.State());
	}

	/// <summary>
	/// A restore at this window's gate: its Google sign-in, held in memory until the restore succeeds (M4), its own client, the restore,
	/// and that client's retry of a refresh that got no answer, as the engine does for the vault's client (I3; null without a scheduler).
	/// </summary>
	private sealed class Restoring(HeldSyncSignIn signIn, SyncClient client, SyncRestore restore, LostRefreshRetry? retry) : IDisposable
	{
		public HeldSyncSignIn SignIn { get; } = signIn;
		public SyncRestore Restore { get; } = restore;
		public LostRefreshRetry? Retry { get; } = retry;

		/// <summary>Held by the step running (N5): a second one is refused, not queued. A retired restore keeps it held for good (N7).</summary>
		public SemaphoreSlim Step { get; } = new(1, 1);

		/// <summary>
		/// A newer restore sign-in replaced this one, or was refused: its refresh token is revoked in the background, best effort (Ruling
		/// 49; N6), then the client goes. A revoke that didn't happen is told to <paramref name="warn"/>.
		/// </summary>
		public void Retire(Action<string, Exception> warn)
		{
			Retry?.Dispose();
			_ = RetireInBackgroundAsync(warn);
		}

		private async Task RetireInBackgroundAsync(Action<string, Exception> warn)
		{
			try
			{
				if (!await client.SignOutAsync(CancellationToken.None)) warn("revoke a replaced restore sign-in", new SyncRevokeFailedException("The sync server couldn't be reached to revoke a replaced restore sign-in; it expires unused"));
			}
			catch (Exception error)
			{
				warn("revoke a replaced restore sign-in", error);
			}
			finally
			{
				client.Dispose();
			}
		}

		public void Dispose()
		{
			Retry?.Dispose();
			client.Dispose();
		}
	}

	private Restoring? _restore;

	/// <summary>The restore client's retry of a refresh that got no answer, while one runs: tests wait on it.</summary>
	internal Task RestoreRefreshRetrying => _restore?.Retry?.Retrying ?? Task.CompletedTask;

	/// <summary>Whether this restore step turned the vault on, so a step that fails turns it off again.</summary>
	private bool _restoreTurnedVaultOn;

	/// <summary>
	/// Restore from sync's first step at the gate: the Google sign-in as Turn on sync does it, kept in memory by the restore's own client.
	/// Nothing on this PC changes yet: the vault is turned on, and the sign-in kept in its sync.json, only once a restore succeeds (M4).
	/// Refused with <see cref="SyncRestoreBusyException"/> while a step of the restore it would replace is running (N7).
	/// </summary>
	private async Task<object?> SignInToRestoreAsync()
	{
		var setup = _app.Sync ?? throw new SyncNotSetUpException();
		if (_restore is { Step.CurrentCount: 0 }) throw new SyncRestoreBusyException();
		return await WithGoogleSignInAsync(setup, async (idToken, cancellation) =>
		{
			var held = new HeldSyncSignIn();
			var client = new SyncClient(setup.Server, held, setup.Handler, setup.Warn);
			SyncAccount account;
			try
			{
				account = await client.SignInAsync(idToken, cancellation);
			}
			catch
			{
				client.Dispose();
				throw;
			}
			var retry = _app.SyncScheduler is { } scheduler ? new LostRefreshRetry(client, scheduler, _app.Clock, _host.Warn) : null;
			var next = new Restoring(held, client, new SyncRestore(_app.Account, VaultForRestore, client, held), retry);
			var replaced = _restore;
			// A step that started during the Google sign-in keeps its restore; taking its step lock retires it for good otherwise.
			if ((replaced is not null && !replaced.Step.Wait(0)) || Interlocked.CompareExchange(ref _restore, next, replaced) != replaced)
			{
				next.Retire(_host.Warn);
				throw new SyncRestoreBusyException();
			}
			replaced?.Retire(_host.Warn);
			return new SyncState(SignedIn: true, account.Email);
		}, () => SyncState.SignedOut());
	}

	/// <summary>The vault a restore writes, turned on now if the setting is off. A vault that's on but unreadable stays as it is (N2).</summary>
	private VaultStore VaultForRestore()
	{
		if (_app.Vaults.Current is null && _app.Settings.Get("passwordAutofill")?.GetValue<bool>() != true)
		{
			_app.Settings.Set("passwordAutofill", true);
			_restoreTurnedVaultOn = true;
		}
		return Vault();
	}

	/// <summary>
	/// One restore step, one at a time per window's restore (N5): a step while another runs raises <see cref="SyncRestoreBusyException"/>.
	/// A step that fails leaves passwordAutofill as it found it.
	/// </summary>
	private async Task<AccountOutcome> RestoreStepAsync(Func<Restoring, Task> step)
	{
		var restoring = _restore ?? throw new NotSignedInToSyncException();
		if (!restoring.Step.Wait(0)) throw new SyncRestoreBusyException();
		try
		{
			_restoreTurnedVaultOn = false;
			await step(restoring);
		}
		catch (Exception error)
		{
			if (_restoreTurnedVaultOn) _app.Settings.Set("passwordAutofill", false);
			if (error is AccountException account) return AccountOutcome.Rejected(account.Code);
			throw;
		}
		finally
		{
			restoring.Step.Release();
		}
		return AccountOutcome.Ok(_app.Account.State());
	}

	/// <summary>The account is made, so the vault the restore turned on stays on whatever fails after (Ruling 65).</summary>
	private void KeepVaultOn() => _restoreTurnedVaultOn = false;

	/// <summary>The restore made the account: its sign-in goes into the vault's sync.json through the vault's own client, and the restore ends.</summary>
	private async Task KeepRestoreSignInAsync(Restoring restoring)
	{
		var signedIn = restoring.SignIn.SyncAccount() ?? throw new NotSignedInToSyncException();
		await SyncClient(Vault()).AdoptAsync(signedIn, _closing.Token);
		if (Interlocked.CompareExchange(ref _restore, null, restoring) == restoring) restoring.Dispose();
	}

	private Task<AccountOutcome> RestoreWithPasswordAsync(string password) => RestoreStepAsync(async restoring =>
	{
		await restoring.Restore.RestoreWithPasswordAsync(password, _closing.Token);
		KeepVaultOn();
		await KeepRestoreSignInAsync(restoring);
	});

	private Task<AccountOutcome> RestoreWithRecoveryKeyAsync(string key) =>
		RestoreStepAsync(restoring => restoring.Restore.RestoreWithRecoveryKeyAsync(key, _closing.Token));

	private Task<AccountOutcome> SetPasswordAfterRecoveryAsync(string password) => RestoreStepAsync(async restoring =>
	{
		await restoring.Restore.ChangePasswordAfterRecoveryAsync(password, _closing.Token);
		KeepVaultOn();
		await KeepRestoreSignInAsync(restoring);
	});

	/// <summary>Long enough to pick an account and pass Google's prompts in the browser.</summary>
	private static readonly TimeSpan SyncSignInTimeout = TimeSpan.FromMinutes(5);

	private static NewLogin NewLoginArg(JsonElement login) =>
		new(RequiredText(login, "origin"), RequiredText(login, "username"), RequiredText(login, "password"), RequiredText(login, "title"), RequiredText(login, "note"));

	private static string RequiredText(JsonElement value, string name) =>
		value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var text) && text.ValueKind == JsonValueKind.String ? text.GetString()! : throw new ArgumentException($"A login needs a string {name}");

	private DnsService DnsSetup() => _app.Dns ?? throw new InvalidOperationException("This host can't change DNS");

	private async Task<object?> DiagnoseAsync(string host)
	{
		var net = _app.Net ?? NetServices.Live;
		return await new Diagnose(net.Probes, net.Doh).RunAsync(host, Setting("dohProvider"), CancellationToken.None);
	}

	/// <summary>
	/// The bridge cannot cancel a call and several runs can overlap, so each attempt is pushed with the id the caller gave this run.
	/// The page itself stays here: it is someone else's text, and the tool only needs to know which way worked.
	/// </summary>
	private async Task<object?> OpenLinkAsync(string input, string runId)
	{
		var net = _app.Net ?? NetServices.Live;
		var address = RelayRules.Normalize(input);
		if (_app.Threats?.WouldWarn(address) == true) return WarnBeforeFetching(address);
		var engine = new RelayEngine(net.RelayTransport, Setting("dohProvider"), attempt => _host.Push("relay:attempt", new RelayAttemptEvent(runId, attempt.Strategy, attempt.Status, attempt.Reason)));
		var result = await engine.OpenAsync(address.AbsoluteUri, _consent, CancellationToken.None);
		return new OpenLinkResult(result.Outcome, result.Strategy, address.AbsoluteUri, result.Attempts);
	}

	/// <summary>The tab goes to the warning on its own: Visit anyway there is what opens the address through the relay.</summary>
	private OpenLinkResult WarnBeforeFetching(Uri address)
	{
		if (RelayedContent.Encode(address) is null) Tabs.FocusPage(Tabs.Open(address.AbsoluteUri).Id);
		else OpenRelayedTab(address.AbsoluteUri);
		return new OpenLinkResult(RelayOutcome.Warned, null, address.AbsoluteUri, []);
	}

	private void OpenRelayedTab(string url)
	{
		var opened = Tabs.OpenRelayed(url);
		Tabs.FocusPage(opened.Id);
	}

	private void LoadFailed(TabRecord tab, string description)
	{
		// The engine can report a second, vaguer failure for the same load (an abort after the name failed); it adds nothing the offer doesn't say.
		if (_offered is not null && tab.Id == _offeredTab && LoadFailureOffer.IsOffered(tab.Url, _offered) && LoadFailureOffer.For(tab.Url, description) is null) return;
		// An offer for a page you aren't looking at would be about something else than what is on screen.
		if (tab.Id != Tabs.ActiveId || LoadFailureOffer.For(tab.Url, description) is not { } address)
		{
			_host.Notify(CouldNotLoad(tab.Url, description));
			return;
		}
		_offered = address;
		_offeredTab = tab.Id;
		Shell.OfferedAddress = address;
		_host.OfferUnblock(address);
	}

	private static string CouldNotLoad(string url, string description) => $"Couldn't load {url} ({description})";

	/// <summary>A click on one of the offer's buttons: the tool opens with the address filled in, and nothing starts until a click there.</summary>
	private OpenPanelOutcome ChooseOffer(string tool)
	{
		var address = _offered ?? throw new NoOfferError();
		var panel = tool switch
		{
			"openLink" => $"section:{PanelSection.Unblock}",
			"diagnose" => $"section:{PanelSection.Diagnose}",
			_ => throw new UnknownOfferToolError(tool),
		};
		var outcome = Shell.OpenPanelWithInput(panel, address);
		// Nothing opened: the offer stays, and the caller says why.
		if (outcome != OpenPanelOutcome.Full) DismissOffer();
		return outcome;
	}

	private void DismissOffer()
	{
		EndOffer();
		_host.DismissOffer();
	}

	private void EndOffer()
	{
		_offered = null;
		Shell.OfferedAddress = null;
	}

	private async Task<object?> HistoryMenuAsync(string direction, JsonElement[] args)
	{
		if (Tabs.ActivePage is not { } page) return null;
		var history = await page.HistoryAsync();
		var items = HistoryMenu.Items(history.Entries, history.ActiveIndex, direction);
		var id = history.Entries;
		Menu([.. items.Select(item => new MenuItem(item.Label, () => page.GoToHistoryEntry(id[item.Index].Id)))], args, 1);
		return null;
	}

	/// <summary>Split with New Tab: a blank tab beside the one in front, with the keyboard in its address bar.</summary>
	private void SplitWithNewTab()
	{
		var active = Tabs.ActiveId ?? throw new InvalidOperationException("No tab to split");
		var created = Tabs.Create(BlankUrl);
		Tabs.SplitWith(active, created.Id);
		FocusAddress();
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
		if (key is "tabDropdowns") Stacks.Close();
		if (key is "tabOrganization" or "tabDropdowns")
		{
			Ordering.Reorganize();
			OnTabsChanged();
		}
		if (key is "accent" or "theme" or "customColor") ForEachStartPage(page => page.Run(StartPage.RestyleScript(StartPage.Theme(Setting("accent"), Setting("customColor")))));
		if (key is "startShortcuts" or "searchEngine") ForEachStartPage(page => page.Reload());
		if (key is "showFavouritesBar") _host.Push("library:state", LibraryState());
		if (key is "sidebarPinned") Shell.SetSidebarPinned(Flag("sidebarPinned"));
		if (key is "recentFeatures") Shell.SetRecentFeatures(RecentFeatures());
		if (key is "tabBarPosition" or "verticalTabWidth") Shell.SetSidebarWidth(SidebarWidth());
		if (key is "tabBarPosition" or "verticalTabsCollapsed") Shell.SetCollapsedWidth(CollapsedSidebar());
		if (key is "sidebarPinned" or "showFavouritesBar" or "tabBarPosition" or "verticalTabWidth" or "verticalTabsCollapsed")
		{
			_host.Relayout();
			_host.Push("library:state", LibraryState());
		}
	}

	private object LibraryState() => new
	{
		library = _app.Library.Snapshot,
		panel = _libraryPanel,
		favouritesBar = FavouritesBarShown(),
		offset = Shell.PinnedWidth,
	};

	private Outcome SaveActive(bool favourite)
	{
		if (Tabs.ActiveId is not { } id) return NotApplicable;
		var tab = Tabs.Get(id);
		if (!tab.Url.StartsWith("http:", StringComparison.OrdinalIgnoreCase) && !tab.Url.StartsWith("https:", StringComparison.OrdinalIgnoreCase)) return NotApplicable;
		var title = string.IsNullOrWhiteSpace(tab.Title) ? new Uri(tab.Url).Host : tab.Title.Trim();
		// A favourite is a saved tab: this tab becomes the item's own (SavedTabs).
		var outcome = favourite ? Saved.Favourite(id) : _app.Library.AddReading(title, tab.Url);
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
		RelayedContent.RefuseRelayAddress(url);
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
		var urls = _app.Library.UrlsOf(id).Select(RelayedContent.RefuseRelayAddress).ToList();
		foreach (var url in urls) Tabs.Open(url);
	}

	private void ForEachStartPage(Action<IPage> act)
	{
		foreach (var record in Tabs.Records)
			if (StartPage.IsStartPage(record.Url) && Tabs.PageOf(record.Id) is { } page) act(page);
	}

	/// <summary>Link inspection, then the start page's search and settings links, which are Zing's to handle; no web page may open Settings.</summary>
	private NavigationDecision? InterceptNavigation(string tabId, string from, string to)
	{
		if (_app.Threats?.Check(tabId, from, to) is { } inspected) return inspected;
		switch (StartPage.ClassifyNavigation(from, to, Setting("searchEngine")))
		{
			case StartPageNavigation.Search search:
				if (AddressInput.JsTrim(search.Query).Length == 0) return NavigationDecision.Cancel;
				return new NavigationDecision.Redirect(_app.Address.Resolve(search.Query, Setting("searchEngine")));
			case StartPageNavigation.EditShortcuts:
				Shell.OpenSettings();
				return NavigationDecision.Cancel;
			case StartPageNavigation.Block:
				return NavigationDecision.Cancel;
			default:
				return null;
		}
	}

	/// <summary>The title of the tab whose page this is, when that tab isn't on screen.</summary>
	private string? HiddenTabOf(object page)
	{
		var tab = Tabs.Records.FirstOrDefault(r => ReferenceEquals(Tabs.PageOf(r.Id), page));
		return tab is null || Tabs.IsShown(tab.Id) ? null : tab.Title;
	}

	private void OnTabsChanged()
	{
		Ordering?.ScheduleReorganize();
		Permissions?.Refresh();
		Find?.ActiveChanged();
		Autofill?.PagesChanged();
		SignIn?.PagesChanged();
		Lifecycle?.Reschedule();
		Stacks?.Refresh();
		Overview?.Refresh();
		_host.Push("tabs:state", State());
		TabsChanged?.Invoke();
	}

	private string Setting(string key) => _app.Settings.Get(key)?.GetValue<string>() ?? throw new InvalidDataException($"Setting {key} is missing");

	private PeekSettings PeekSettings() => new(Flag("linkPreviews"), Flag("linkPreviewsShiftClick"), Flag("linkPreviewsFromNewWindows"));

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

	private static double Number(JsonElement[] args, int index)
	{
		var arg = Arg(args, index);
		return arg.ValueKind == JsonValueKind.Number ? arg.GetDouble() : throw new ArgumentException($"Argument {index} must be a number");
	}

	private static Point PointArg(JsonElement[] args, int index)
	{
		var arg = Arg(args, index);
		return new Point(Number(arg, "x"), Number(arg, "y"));
	}

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
internal sealed class LifecycleTabs(TabService tabs, TabOverview overview) : ILifecycleTabs
{
	public string? ActiveId => tabs.ActiveId;
	public bool IsShown(string id) => tabs.IsShown(id);
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

	// A hidden page never finishes a capture; the overview pictured it as it left the screen.
	public Task<string?> CaptureThumbnailAsync(string id) => Task.FromResult(overview.Thumbnail(id));

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
