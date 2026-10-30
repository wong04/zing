import Foundation
import SwiftUI
import WebKit

@MainActor
final class BrowserModel: ObservableObject {
    @Published var tabs: [BrowserTab] = []
    @Published var activeID: UUID?
    @Published var notice: String?

    // MARK: State the keyboard shortcuts drive

    /// ⌘F — the find bar under the toolbar.
    @Published var findVisible = false
    /// Bumped to re-focus the find field when ⌘F is pressed while it's open.
    @Published var findFocusToken = 0
    /// ⌘L — asks the address bar to take focus; it clears this once it has.
    @Published var addressFocusRequested = false
    /// ⌃⌘1 / ⌃⌘2 — the bookmarks or Reading List sidebar.
    @Published var libraryPane: LibraryPane?
    /// ⇧⌘\ — every tab at once.
    @Published var showsOverview = false
    /// ⇧⌘R — the current article, cleaned up.
    @Published var reader: ReaderArticle?
    @Published var downloads: [DownloadItem] = []

    enum LibraryPane: Hashable { case bookmarks, readingList }

    /// ⇧⌘T. Memory only — a list of closed pages on disk would be the
    /// history this browser doesn't keep.
    private var closedTabs = ClosedStack<TabRecord>()
    /// Windows closed this session, for ⇧⌘T once a window has no closed tabs
    /// of its own left to bring back.
    static var closedWindows = ClosedStack<[TabRecord]>(capacity: 5)
    /// Tabs for the next window to open with — set just before ⇧⌘T opens one.
    static var pendingWindowTabs: [TabRecord]?

    /// The first window's browser owns session restore. With several windows,
    /// each saving its own tabs to the one session file would leave whichever
    /// saved last — so only this one restores and saves.
    static weak var primary: BrowserModel?
    var ownsSession: Bool { BrowserModel.primary === self }

    /// Serves the local new-tab page. Held here so the search endpoint can be
    /// updated when the user changes engines in Settings.
    let startPageHandler = StartPageHandler(searchURL: AppSettings.searchEngine.formAction)

    /// The session's data store, and the whole basis of the privacy promise.
    ///
    /// `.nonPersistent()` keeps cookies, local storage, IndexedDB, caches and
    /// service workers in memory only — nothing is ever written to disk, so
    /// there is no profile directory to inspect afterwards and quitting the app
    /// destroys the lot. The trade is real and worth stating: you are signed
    /// out of everything when Krackn quits, because staying signed in *is* a
    /// stored trace.
    ///
    /// What this cannot do is hide that a connection happened from the network
    /// itself — DNS lookups and the TLS server name are on the wire regardless.
    /// Encrypted DNS closes the first; the profile in Network & VPN is what
    /// closes it system-wide.
    private lazy var dataStore: WKWebsiteDataStore = {
        // The persistent option is a compatibility escape hatch, off by
        // default. Some sites behave differently without durable storage, and
        // being able to rule that in or out in one relaunch is worth more than
        // insisting the private path is the only path.
        AppSettings.persistentProfile ? .default() : .nonPersistent()
    }()

    /// True when this session is leaving nothing behind — surfaced in the UI so
    /// the privacy claim can never be shown while the escape hatch is on.
    var isEphemeral: Bool { !dataStore.isPersistent }

    private lazy var configuration: WKWebViewConfiguration = {
        let config = WKWebViewConfiguration()
        config.setURLSchemeHandler(startPageHandler, forURLScheme: StartPage.scheme)
        config.websiteDataStore = dataStore
        config.defaultWebpagePreferences.allowsContentJavaScript = true
        config.allowsAirPlayForMediaPlayback = true
        config.mediaTypesRequiringUserActionForPlayback = []
        // Off by default in WKWebView: `document.fullscreenEnabled` reads
        // false, so YouTube (and every other player) hides or disables its
        // fullscreen button. Popups inherit this through WebKit's own
        // configuration.
        config.preferences.isElementFullscreenEnabled = true
        // Canvas rasterisation path. Off the GPU by default so canvas-drawn
        // text is rasterised at the display's real scale — see WebKitTuning.
        WebKitTuning.setAcceleratedCanvas(AppSettings.acceleratedCanvas,
                                          on: config.preferences)
        // Content blocking rules are compiled once and attached below.
        // Keychain AutoFill: the field detector, in its own content world.
        config.userContentController.addUserScript(KeychainAutofill.userScript)
        config.userContentController.add(credentialRouter, contentWorld: KeychainAutofill.world,
                                         name: KeychainAutofill.handlerName)
        return config
    }()

    private lazy var credentialRouter: CredentialFieldRouter = {
        let router = CredentialFieldRouter()
        router.tabForWebView = { [weak self] web in self?.tabs.first { $0.webView === web } }
        return router
    }()

    var active: BrowserTab? {
        tabs.first { $0.id == activeID }
    }

    /// True when the visible tab is the local new-tab page — the condition for
    /// handing it keyboard focus so typing goes straight into its search box.
    var activeIsStartPage: Bool {
        guard let active else { return false }
        return active.webView.url?.scheme == StartPage.scheme
            || active.webView.url == nil
    }

    var organization: TabOrganization {
        TabOrganization(
            rawValue: UserDefaults.standard.string(forKey: AppSettings.Key.tabOrganization) ?? ""
        ) ?? .manual
    }

    /// Index of the first unpinned tab — where the pinned/unpinned divider goes.
    var pinnedCount: Int { tabs.prefix { $0.isPinned }.count }

    private var sweepTimer: Timer?
    private var lastSweep = Date()

    init() {
        installTrackerBlocking()
        if BrowserModel.primary == nil { BrowserModel.primary = self }
        // A window reopened with ⇧⌘T comes back with its tabs.
        if let reopened = BrowserModel.pendingWindowTabs {
            BrowserModel.pendingWindowTabs = nil
            restoreSession(reopened, announce: false)
            startSweeper()
            observeTermination()
            return
        }
        // Reopen last session's tabs before creating a blank one, so the user
        // doesn't get an empty tab shoved in front of their restored work.
        let restored = AppSettings.restoreTabs && ownsSession ? SessionStore.load() : []
        if restored.isEmpty {
            newTab(url: StartPage.url.absoluteString)
        } else {
            restoreSession(restored)
        }
        startSweeper()
        observeTermination()
    }

    // MARK: - Session

    private var saveWorkItem: DispatchWorkItem?

    private func restoreSession(_ records: [TabRecord], announce: Bool = true) {
        for record in records {
            let tab = BrowserTab(configuration: configuration)
            wire(tab)
            tabs.append(tab)
            tab.restore(from: record)
        }
        // Reselect whichever tab was in front, falling back to the first.
        let activeIndex = records.firstIndex(where: \.isActive) ?? 0
        let selected = tabs.indices.contains(activeIndex) ? tabs[activeIndex] : tabs.first
        activeID = selected?.id
        selected?.lastAccessed = Date()
        if announce {
            post("Reopened \(tabs.count) tab\(tabs.count == 1 ? "" : "s") from last time.")
        }
    }

    /// Writes the session out, coalesced. Navigation fires this constantly, and
    /// serialising every tab's full interaction state on each keystroke of a
    /// single-page app would be absurd.
    func scheduleSessionSave() {
        guard AppSettings.restoreTabs, ownsSession else { return }
        saveWorkItem?.cancel()
        let work = DispatchWorkItem { [weak self] in
            Task { @MainActor in self?.saveSessionNow() }
        }
        saveWorkItem = work
        DispatchQueue.main.asyncAfter(deadline: .now() + 1.5, execute: work)
    }

    func saveSessionNow() {
        guard AppSettings.restoreTabs, ownsSession else { return }
        // Stamp the front tab so restore reselects it rather than always
        // landing on the leftmost one.
        let active = activeID
        SessionStore.save(tabs.map { tab in
            var record = tab.record
            record.isActive = (tab.id == active)
            return record
        })
    }

    /// Quitting is the moment that matters most, and a debounced save would be
    /// cancelled by the process exiting.
    private func observeTermination() {
        NotificationCenter.default.addObserver(
            forName: NSApplication.willTerminateNotification,
            object: nil, queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated { self?.saveSessionNow() }
        }
    }

    /// Everything a freshly built tab needs hooked up. Shared by `newTab` and
    /// session restore so a restored tab isn't quietly missing its callbacks.
    /// Set when a new-tab page asks for its shortcuts to be edited; the
    /// window opens Settings › New Tab and clears it.
    @Published var shortcutSettingsRequested = false

    private func wire(_ tab: BrowserTab) {
        tab.onEditShortcuts = { [weak self] in self?.shortcutSettingsRequested = true }
        tab.onNewTabRequested = { [weak self] requested in
            self?.newTab(url: requested?.absoluteString)
        }
        tab.onDownload = { [weak self] message in
            self?.post(message)
        }
        tab.onNavigationCheck = { url in
            ThreatScanner.shared.vetDomainInBackground(url.host ?? "")
            return ThreatScanner.shared.checkNavigation(url)
        }
        tab.onNavigated = { [weak self] in
            self?.scheduleSessionSave()
            self?.scheduleReorganize()
        }
        tab.onCreatePopup = { [weak self, weak tab] configuration in
            guard let self else { return nil }
            let popup = BrowserTab(configuration: configuration)
            self.wire(popup)
            popup.openerID = tab?.id
            // Right after the page that opened it, where you'd expect it.
            let index = tab.flatMap { t in self.tabs.firstIndex { $0.id == t.id } }
            withAnimation(Motion.standard) {
                if let index { self.tabs.insert(popup, at: index + 1) } else { self.tabs.append(popup) }
            }
            self.activate(popup)
            return popup.webView
        }
        tab.onCloseRequested = { [weak self, weak tab] in
            guard let self, let tab else { return }
            // Back to the page that opened it — the site you were signing in to.
            let opener = tab.openerID.flatMap { id in self.tabs.first { $0.id == id } }
            withAnimation(Motion.standard) { self.close(tab) }
            if let opener { self.activate(opener) }
        }
        tab.onOpenInNewTab = { [weak self] url, activate in
            self?.newTab(url: url.absoluteString, activate: activate)
        }
        tab.onAddToReadingList = { [weak self] url in
            LibraryStore.shared.addToReadingList(title: url.host ?? url.absoluteString,
                                                 url: url.absoluteString)
            self?.post("Added to Reading List.")
        }
        tab.onDownloadEvent = { [weak self] event in
            self?.record(event)
        }
    }

    private func record(_ event: DownloadEvent) {
        withAnimation(Motion.standard) {
            switch event {
            case .started(let id, let file):
                downloads.insert(DownloadItem(id: id, file: file, state: .running), at: 0)
            case .finished(let id, let notice):
                if let i = downloads.firstIndex(where: { $0.id == id }) { downloads[i].state = .done(notice) }
            case .failed(let id, let message):
                if let i = downloads.firstIndex(where: { $0.id == id }) { downloads[i].state = .failed(message) }
            }
        }
    }

    // MARK: - Shortcut actions

    func selectNextTab() {
        guard let i = tabs.firstIndex(where: { $0.id == activeID }),
              let n = TabNavigation.next(after: i, count: tabs.count) else { return }
        activate(tabs[n])
    }

    func selectPreviousTab() {
        guard let i = tabs.firstIndex(where: { $0.id == activeID }),
              let n = TabNavigation.previous(before: i, count: tabs.count) else { return }
        activate(tabs[n])
    }

    func selectTab(number: Int) {
        guard let n = TabNavigation.index(forShortcut: number, count: tabs.count) else { return }
        activate(tabs[n])
    }

    /// ⌥-click a tab's close button.
    func closeOthers(keeping tab: BrowserTab) {
        for other in tabs where other.id != tab.id && !other.isPinned { close(other) }
        activate(tab)
    }

    /// ⇧⌘T. Returns false when there's nothing left to reopen in this window.
    @discardableResult
    func reopenClosedTab() -> Bool {
        guard let record = closedTabs.pop() else { return false }
        let tab = BrowserTab(configuration: configuration)
        wire(tab)
        tabs.append(tab)
        tab.restore(from: record)
        activate(tab)
        reorganize()
        scheduleSessionSave()
        return true
    }

    var canReopenTab: Bool { !closedTabs.isEmpty }

    /// Called as the window closes, so ⇧⌘T can bring the whole window back.
    func rememberWindow() {
        let records = tabs.map(\.record).filter { $0.isRestorable }
        if !records.isEmpty { BrowserModel.closedWindows.push(records) }
        if BrowserModel.primary === self { BrowserModel.primary = nil }
    }

    func showFind() {
        withAnimation(Motion.quick) { findVisible = true }
        findFocusToken += 1
    }

    /// Kept here, not in the bar, so ⌘G and ⇧⌘G work from the menu bar.
    @Published var findQuery = ""
    @Published var findCount = 0
    @Published var findIndex = 0
    private var findTask: Task<Void, Never>?

    /// A new query: count the matches and select the first.
    func runFind() {
        findTask?.cancel()
        let query = findQuery
        findTask = Task { [weak self] in
            try? await Task.sleep(for: .milliseconds(120))
            guard let self, !Task.isCancelled, let tab = self.active else { return }
            guard !query.isEmpty else { self.findCount = 0; self.findIndex = 0; return }
            let count = await tab.countMatches(query)
            let found = await tab.find(query)
            guard !Task.isCancelled else { return }
            self.findCount = found ? max(count, 1) : 0
            self.findIndex = found ? 1 : 0
        }
    }

    /// ⌘G / ⇧⌘G — opens the bar if nothing's been searched for yet.
    func findNext(backwards: Bool = false) {
        guard !findQuery.isEmpty, let tab = active else { showFind(); return }
        Task {
            guard await tab.find(findQuery, backwards: backwards), findCount > 0 else { return }
            findIndex = backwards ? (findIndex - 2 + findCount) % findCount + 1
                                  : findIndex % findCount + 1
        }
    }

    func closeFind() {
        withAnimation(Motion.quick) { findVisible = false }
        active?.webView.evaluateJavaScript("window.getSelection().removeAllRanges()",
                                          completionHandler: nil)
    }

    func toggleLibrary(_ pane: LibraryPane) {
        withAnimation(Motion.surface) { libraryPane = libraryPane == pane ? nil : pane }
    }

    func toggleOverview() {
        active?.captureThumbnail()
        withAnimation(Motion.surface) { showsOverview.toggle() }
    }

    func toggleReader() {
        if reader != nil {
            withAnimation(Motion.standard) { reader = nil }
            return
        }
        Task {
            if let article = await active?.readerArticle() {
                withAnimation(Motion.surface) { reader = article }
            } else {
                post("Reader isn't available for this page.")
            }
        }
    }

    func addBookmark() {
        guard let tab = active, let url = tab.webView.url,
              url.scheme?.hasPrefix("http") == true else { return }
        if LibraryStore.shared.isFavourite(url.absoluteString) {
            post("Already in Favourites.")
        } else {
            LibraryStore.shared.addFavourite(title: tab.title, url: url.absoluteString)
            post("Added to Favourites.")
        }
    }

    func addToReadingList() {
        guard let tab = active, let url = tab.webView.url,
              url.scheme?.hasPrefix("http") == true else { return }
        LibraryStore.shared.addToReadingList(title: tab.title, url: url.absoluteString)
        post("Added to Reading List.")
    }

    /// True while a tab is being dragged, so a page finishing its load
    /// mid-drag can't re-sort the strip out from under the cursor.
    var isDraggingTab = false {
        didSet { if !isDraggingTab { scheduleReorganize() } }
    }
    private var pendingReorganize: Task<Void, Never>?

    /// Re-sorts once a tab's site or title has settled.
    ///
    /// This is what was missing when "By site" appeared to do nothing: tabs
    /// were sorted when created, but a new tab has no site yet — it gets one
    /// a moment later when the page loads, and nothing sorted again after
    /// that. A short debounce folds a redirect chain into one move instead of
    /// a tab hopping across the strip several times.
    func scheduleReorganize() {
        guard organization != .manual else { return }
        pendingReorganize?.cancel()
        pendingReorganize = Task { @MainActor [weak self] in
            try? await Task.sleep(for: .milliseconds(350))
            guard let self, !Task.isCancelled, !self.isDraggingTab else { return }
            withAnimation(Motion.standard) { self.reorganize() }
        }
    }

    // MARK: - Tabs

    @discardableResult
    func newTab(url: String? = nil, activate: Bool = true) -> BrowserTab {
        let tab = BrowserTab(configuration: configuration)
        wire(tab)
        tabs.append(tab)
        if activate {
            active?.captureThumbnail()
            reader = nil
            findVisible = false
            activeID = tab.id
            tab.lastAccessed = Date()
        }
        if let url { tab.go(to: url) }
        reorganize()
        scheduleSessionSave()
        return tab
    }

    func post(_ message: String) {
        notice = message
        Task { [weak self] in
            try? await Task.sleep(for: .seconds(5))
            if self?.notice == message { self?.notice = nil }
        }
    }

    func activate(_ tab: BrowserTab) {
        // The tab being left is still on screen for this moment — the only
        // moment its picture can be taken for the overview.
        if tab.id != activeID { active?.captureThumbnail() }
        // Find and Reader belong to the page they were opened on.
        if tab.id != activeID {
            reader = nil
            findVisible = false
        }
        activeID = tab.id
        tab.lastAccessed = Date()
        // "Last opened" ordering has to settle after the click, or the tab
        // would slide out from under the cursor mid-press.
        if organization == .recentlyUsed {
            Task { @MainActor in
                try? await Task.sleep(for: .milliseconds(220))
                withAnimation(.spring(response: 0.3, dampingFraction: 0.85)) {
                    self.reorganize()
                }
            }
        }
    }

    /// Moves a tab between slots, for drag-to-reorder.
    ///
    /// A tab can only move within its own section: dragging an unpinned tab
    /// into the pinned run would silently un-pin it by position alone, which
    /// isn't what grabbing a tab means.
    func moveTab(from: Int, to: Int) {
        guard tabs.indices.contains(from), tabs.indices.contains(to), from != to else { return }
        let pinned = pinnedCount
        let movingPinned = tabs[from].isPinned
        let clamped = movingPinned
            ? min(to, max(pinned - 1, 0))
            : max(to, pinned)
        guard tabs.indices.contains(clamped), clamped != from else { return }
        let item = tabs.remove(at: from)
        tabs.insert(item, at: clamped)
    }

    // MARK: Dropdown-style reordering

    /// The stacks as the Dropdown style shows them right now.
    var displayedStacks: [TabOrganizer.Stack] {
        TabOrganizer.stacks(tabs.map { $0.snapshot(isActive: $0.id == activeID) },
                            alphabetical: organization == .alphabetical)
    }

    /// Lays the unpinned tabs out as these stacks, in this order; pinned tabs
    /// stay in front. Refused unless it accounts for every tab exactly once.
    func arrange(stacks: [[UUID]]) {
        let lookup = Dictionary(uniqueKeysWithValues: tabs.map { ($0.id, $0) })
        let loose = stacks.flatMap { $0 }.compactMap { lookup[$0] }
        let pinned = tabs.filter(\.isPinned)
        guard pinned.count + loose.count == tabs.count,
              Set(loose.map(\.id)).count == loose.count,
              !loose.contains(where: \.isPinned) else { return }
        let next = pinned + loose
        if next.map(\.id) != tabs.map(\.id) { tabs = next }
    }

    /// Before a hand-made change in a sorted arrangement: makes what's on
    /// screen the manual order, so switching to manual doesn't move anything.
    func freezeDisplayedOrder() {
        arrange(stacks: displayedStacks.map(\.ids))
    }

    /// A page dragged within its stack's dropdown. The stack's pages swap
    /// among the places they already hold, so the rest of the strip — and the
    /// stack's own place in it — stays put.
    func move(_ id: UUID, within ids: [UUID], to index: Int) {
        let slots = tabs.indices.filter { ids.contains(tabs[$0].id) }
        let current = slots.map { tabs[$0].id }
        let reordered = TabOrganizer.moving(id, in: current, to: index)
        guard reordered != current else { return }
        let lookup = Dictionary(uniqueKeysWithValues: tabs.map { ($0.id, $0) })
        var next = tabs
        for (slot, tabID) in zip(slots, reordered) {
            guard let tab = lookup[tabID] else { return }
            next[slot] = tab
        }
        tabs = next
    }

    func togglePin(_ tab: BrowserTab) {
        tab.isPinned.toggle()
        withAnimation(.spring(response: 0.3, dampingFraction: 0.82)) {
            // Pinned tabs live at the front; unpinning drops the tab to the
            // start of the loose section rather than wherever it used to be,
            // because that old position no longer means anything.
            guard let index = tabs.firstIndex(where: { $0.id == tab.id }) else { return }
            let item = tabs.remove(at: index)
            tabs.insert(item, at: tab.isPinned ? pinnedCount : pinnedCount)
            reorganize()
        }
        objectWillChange.send()
        scheduleSessionSave()
    }

    /// Re-sorts the strip according to the chosen strategy. A no-op in manual
    /// mode, which is why dragging still works there.
    func reorganize() {
        let strategy = organization
        guard strategy != .manual else { return }
        let snapshots = tabs.map { $0.snapshot(isActive: $0.id == activeID) }
        let order = TabOrganizer.order(snapshots, by: strategy)
        let lookup = Dictionary(uniqueKeysWithValues: tabs.map { ($0.id, $0) })
        let reordered = order.compactMap { lookup[$0] }
        guard reordered.count == tabs.count,
              reordered.map(\.id) != tabs.map(\.id) else { return }
        tabs = reordered
    }

    /// Re-renders any tab currently showing the start page, so a theme or
    /// search-engine change takes effect immediately.
    /// Recolours open new-tab pages for a new accent without reloading them —
    /// a reload would clear whatever was typed in the search box.
    func restyleStartPages() {
        guard let data = try? JSONEncoder().encode(StartPage.themeCSS),
              let literal = String(data: data, encoding: .utf8) else { return }
        let script = "(() => { const s = document.getElementById('krackn-theme'); if (s) s.textContent = \(literal); })()"
        for tab in tabs where tab.webView.url?.scheme == StartPage.scheme {
            tab.webView.evaluateJavaScript(script, completionHandler: nil)
        }
    }

    func reloadStartPages() {
        for tab in tabs where tab.webView.url?.scheme == StartPage.scheme {
            tab.webView.reload()
        }
    }

    func close(_ tab: BrowserTab) {
        guard let index = tabs.firstIndex(where: { $0.id == tab.id }) else { return }
        let domain = tab.siteDomain
        let record = tab.record
        if record.isRestorable { closedTabs.push(record) }
        tab.teardown()
        tabs.remove(at: index)

        // Everything the tab left behind goes with it. The store is already
        // in-memory only, so this is about not letting one closed tab's cookies
        // linger for the rest of the session — "once the tab closes, it's gone".
        purgeData(forDomain: domain)

        if tabs.isEmpty {
            newTab(url: StartPage.url.absoluteString)
        } else if activeID == tab.id {
            let next = tabs[min(index, tabs.count - 1)]
            activeID = next.id
            next.lastAccessed = Date()
        }
        scheduleSessionSave()
    }

    /// Drops stored data for a site once no open tab is using it.
    private func purgeData(forDomain domain: String) {
        guard !domain.isEmpty else { return }
        guard !tabs.contains(where: { $0.siteDomain == domain }) else { return }
        let types = WKWebsiteDataStore.allWebsiteDataTypes()
        dataStore.fetchDataRecords(ofTypes: types) { [weak self] records in
            let matching = records.filter {
                $0.displayName == domain || $0.displayName.hasSuffix(".\(domain)")
            }
            guard !matching.isEmpty else { return }
            self?.dataStore.removeData(ofTypes: types, for: matching) {}
        }
    }

    /// Wipes the entire session. Bound to the panic button in Settings.
    func clearEverything() {
        let types = WKWebsiteDataStore.allWebsiteDataTypes()
        dataStore.removeData(ofTypes: types, modifiedSince: .distantPast) {}
        for tab in tabs where tab.webView.url?.scheme != StartPage.scheme {
            tab.goHome()
        }
        // The saved tab list is browsing data too — leaving it behind would
        // make "clear everything" a lie.
        SessionStore.clear()
        post("Session data cleared.")
    }

    // MARK: - Tracker blocking

    /// Compiles a content-blocking ruleset so third-party trackers are refused
    /// by WebKit itself, before a request is made. This is a different layer
    /// from stripping tracking parameters off the URL: that cleans the link you
    /// follow, this stops the page loading beacons once you're there.
    private func installTrackerBlocking() {
        guard AppSettings.blockTrackers else { return }
        let rules = TrackerRules.json
        WKContentRuleListStore.default()?.compileContentRuleList(
            forIdentifier: "krackn-trackers", encodedContentRuleList: rules
        ) { [weak self] list, error in
            guard let list, error == nil else { return }
            Task { @MainActor in
                self?.configuration.userContentController.add(list)
                for tab in self?.tabs ?? [] {
                    tab.webView.configuration.userContentController.add(list)
                }
            }
        }
    }

    // MARK: - Auto-close

    private func startSweeper() {
        // A minute is fine: the shortest rule is an hour, and the scheduled
        // sweep is caught by interval rather than by exact instant.
        sweepTimer = Timer.scheduledTimer(withTimeInterval: 60, repeats: true) { [weak self] _ in
            Task { @MainActor in self?.sweep() }
        }
    }

    func sweep(now: Date = Date()) {
        let policy = Self.currentPolicy()
        guard policy.enabled else { lastSweep = now; return }

        let snapshots = tabs.map { $0.snapshot(isActive: $0.id == activeID) }
        var doomed = TabLifecycle.tabsToClose(snapshots, policy: policy, now: now)

        if TabLifecycle.scheduleElapsed(policy: policy, since: lastSweep, now: now) {
            doomed = TabLifecycle.tabsToCloseOnSchedule(snapshots)
        }
        lastSweep = now

        guard !doomed.isEmpty else { return }
        let set = Set(doomed)
        for tab in tabs where set.contains(tab.id) { close(tab) }
        post("Closed \(set.count) inactive tab\(set.count == 1 ? "" : "s").")
    }

    static func currentPolicy() -> AutoClosePolicy {
        let defaults = UserDefaults.standard
        return AutoClosePolicy(
            enabled: defaults.bool(forKey: AppSettings.Key.autoCloseEnabled),
            rule: AutoCloseRule(
                rawValue: defaults.string(forKey: AppSettings.Key.autoCloseRule) ?? "") ?? .day,
            customHours: defaults.double(forKey: AppSettings.Key.autoCloseCustomHours),
            atTimeEnabled: defaults.bool(forKey: AppSettings.Key.autoCloseAtTimeEnabled),
            hour: defaults.integer(forKey: AppSettings.Key.autoCloseHour),
            minute: defaults.integer(forKey: AppSettings.Key.autoCloseMinute))
    }
}

/// A compact content-blocking ruleset covering the trackers that account for
/// the overwhelming majority of third-party requests.
///
/// Deliberately small and readable rather than a bundled 100k-entry list: a
/// giant list can't be reviewed, goes stale the moment it ships, and would be a
/// third-party dependency in everything but name. These are blocked as
/// third-party only, so a site's own first-party analytics still work and
/// nothing breaks the page you actually asked for.
enum TrackerRules {
    static let domains = [
        "doubleclick.net", "googlesyndication.com", "googletagservices.com",
        "google-analytics.com", "googletagmanager.com", "adservice.google.com",
        "facebook.net", "connect.facebook.net", "graph.facebook.com",
        "scorecardresearch.com", "quantserve.com", "outbrain.com", "taboola.com",
        "criteo.com", "criteo.net", "adnxs.com", "rubiconproject.com",
        "pubmatic.com", "openx.net", "casalemedia.com", "sharethrough.com",
        "amazon-adsystem.com", "adsrvr.org", "bluekai.com", "krxd.net",
        "mathtag.com", "bidswitch.net", "smartadserver.com", "teads.tv",
        "hotjar.com", "fullstory.com", "mouseflow.com", "luckyorange.com",
        "segment.io", "segment.com", "mixpanel.com", "amplitude.com",
        "branch.io", "appsflyer.com", "adjust.com", "kochava.com",
        "chartbeat.com", "parsely.com", "newrelic.com", "nr-data.net",
        "onesignal.com", "pushcrew.com", "taboolasyndication.com",
    ]

    /// Builds the WebKit content-blocker JSON.
    ///
    /// The escaping is worth spelling out: the `url-filter` value is a regex
    /// *inside* a JSON string, so a literal dot needs a backslash for the regex
    /// and that backslash needs doubling for JSON. The raw-string literals
    /// below hold exactly the characters that end up in the JSON text.
    static var json: String {
        let rules = domains.map { domain -> String in
            let escaped = domain.replacingOccurrences(of: ".", with: #"\\."#)
            let filter = #"^https?://([^/]+\\.)?"# + escaped
            return #"{"trigger":{"url-filter":"\#(filter)",""#
                + #""load-type":["third-party"]},"action":{"type":"block"}}"#
        }
        return "[" + rules.joined(separator: ",") + "]"
    }
}
