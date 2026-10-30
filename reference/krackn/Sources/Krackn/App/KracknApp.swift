import SwiftUI
import AppKit

/// Handles the things a `Scene` can't: the quit confirmation.
final class AppDelegate: NSObject, NSApplicationDelegate {
    // Links from other apps are *not* handled here. With an
    // `application(_:open:)` and no scene preference, SwiftUI opened a brand
    // new window for every link — measured: two links, three windows. The
    // window's content now claims links (`AppGate`), so they land as a tab in
    // the window you already have.

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool {
        true
    }
}

@main
struct KracknApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var delegate
    @AppStorage(AppSettings.Key.theme) private var themeRaw = AppTheme.system.rawValue

    init() {
        // Before any @AppStorage is read, so a returning user's settings and
        // account survive the rename from "All in One".
        Migration.runIfNeeded()
        AppSettings.registerDefaults()
        _ = AppFont.family
    }

    var body: some Scene {
        WindowGroup(id: "main") {
            WindowRoot(themeRaw: $themeRaw)
        }
        .windowStyle(.hiddenTitleBar)
        .commands { KracknCommands() }
    }
}

/// Everything one window holds that must outlive a rebuild of its views.
///
/// Changing the accent rebuilds the window's whole view tree (`WindowRoot`),
/// because `Palette` resolves through the accent at read time. That used to
/// rebuild `RootView` too — and with it every `@StateObject`: the browser
/// model was thrown away, so every tab was replaced and every page reloaded,
/// and Settings (where the accent is changed) closed itself. The window's
/// state lives here instead, created once above the rebuilt part.
@MainActor
final class WindowSession: ObservableObject {
    let browser = BrowserModel()
    let viewer = FileViewerModel()
    let converter = ConvertModel()
    let backgroundRemover = BackgroundRemoverModel()
    let panels = PanelStore()
    let recents = RecentFeatures()
    let edge = EdgeRevealMonitor()
    let actions = WindowActions()
    let inbox = LinkInbox()
    /// Where the window was, so a rebuild puts it back there.
    var route: Route = .browser
    var showSettings = false
    var settingsCategory: SettingsCategory = .general
}

/// One window. Keeps the session and redraws everything beneath it when the
/// accent changes.
struct WindowRoot: View {
    @Binding var themeRaw: String
    @AppStorage(AppSettings.Key.accent) private var accentRaw = AccentTheme.yellow.rawValue
    @AppStorage(AppSettings.Key.startShortcuts) private var startShortcuts = ""
    @StateObject private var session = WindowSession()
    private var theme: AppTheme { AppTheme(rawValue: themeRaw) ?? .system }

    var body: some View {
        AppGate(themeRaw: $themeRaw, session: session)
            // `Palette` resolves through the selected accent at read time, so
            // the views have to be rebuilt when it changes — keyed here, below
            // the session, so only views are rebuilt and never the tabs.
            .id(accentRaw)
            .frame(minWidth: 900, minHeight: 640)
            .background(Palette.background)
            .background(WindowChrome(appearance: theme.nsAppearance))
            .preferredColorScheme(theme.colorScheme)
            .ignoresSafeArea()
            .onChange(of: accentRaw) { _, _ in session.browser.restyleStartPages() }
            // New shortcuts show at once in every open new-tab page — here,
            // because the browser page may not be on screen.
            .onChange(of: startShortcuts) { _, _ in session.browser.reloadStartPages() }
            // Here, not in RootView: an accent change makes RootView disappear
            // too, and that must not count as the window closing — it would
            // file the window under ⇧⌘T, stop the edge monitor and, by
            // dropping `BrowserModel.primary`, stop the session being saved.
            .onDisappear {
                session.edge.stop()
                // So ⇧⌘T can bring this window back.
                session.browser.rememberWindow()
            }
    }
}

/// The app's larger features. Most now live in the Toolbox; the enum stays
/// because floating panels and the recents list identify them by it.
enum Section: String, CaseIterable, Identifiable, Hashable {
    case browser = "Browser"
    case viewer = "Files"
    case convert = "Convert"
    case utilities = "Toolbox"
    case download = "Download"
    case unblock = "Open a Link"
    case diagnose = "Diagnose"
    case network = "Network & VPN"
    case security = "Security"
    case settings = "Settings"

    var id: String { rawValue }

    var icon: String {
        switch self {
        case .browser: return "globe"
        case .viewer: return "doc.viewfinder"
        case .convert: return "arrow.triangle.2.circlepath"
        case .utilities: return "square.grid.2x2"
        case .download: return "arrow.down.circle"
        case .unblock: return "arrow.triangle.branch"
        case .diagnose: return "stethoscope"
        case .network: return "lock.shield"
        case .security: return "checkmark.shield"
        case .settings: return "gearshape"
        }
    }
}

struct RootView: View {
    @Binding var themeRaw: String
    let session: WindowSession
    @ObservedObject var inbox: LinkInbox
    @State private var route: Route
    /// Which way the last navigation went, so pages slide in from the side
    /// they logically came from.
    @State private var navigatingBack = false
    @AppStorage(AppSettings.Key.sidebarPinned) private var pinned = true
    @ObservedObject private var edge: EdgeRevealMonitor
    @ObservedObject private var recents: RecentFeatures
    @State private var showSettings: Bool
    /// Settings was already open when this view was built — an accent change
    /// rebuilt it — so it reappears in place rather than animating in.
    private let settingsAlreadyOpen: Bool
    @State private var minimizeTarget = CGPoint(x: 40, y: 320)
    /// Hands a file from the viewer's "Convert" shortcut to the Convert page.
    @State private var pendingConvert: URL?
    /// Hands a file dropped on the sidebar's Files row to the viewer.
    @State private var pendingPreview: URL?
    /// The browser lives here so its tabs, history and logins survive
    /// switching pages. Each tab keeps its own WKWebView, which is re-attached
    /// to the view hierarchy when you come back.
    @ObservedObject private var browser: BrowserModel
    /// Files, Convert and the background remover keep their state across
    /// navigation too — and across being popped out into a window and back.
    @ObservedObject private var viewer: FileViewerModel
    @ObservedObject private var converter: ConvertModel
    @ObservedObject private var backgroundRemover: BackgroundRemoverModel
    @ObservedObject private var panels: PanelStore
    /// Bumped to clear the Toolbox's search when its row is clicked again.
    @State private var toolboxResetToken = 0
    @State private var hostSize: CGSize = .zero
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @Environment(\.openWindow) private var openWindow
    /// This window's end of the menu bar: commands arrive here when this
    /// window is in front.
    @ObservedObject private var actions: WindowActions
    @State private var window: NSWindow?
    /// Bumped by ⌘F on the Toolbox, which focuses its search field.
    @State private var toolboxFocusToken = 0

    init(themeRaw: Binding<String>, session: WindowSession) {
        _themeRaw = themeRaw
        self.session = session
        inbox = session.inbox
        _edge = ObservedObject(wrappedValue: session.edge)
        _recents = ObservedObject(wrappedValue: session.recents)
        _browser = ObservedObject(wrappedValue: session.browser)
        _viewer = ObservedObject(wrappedValue: session.viewer)
        _converter = ObservedObject(wrappedValue: session.converter)
        _backgroundRemover = ObservedObject(wrappedValue: session.backgroundRemover)
        _panels = ObservedObject(wrappedValue: session.panels)
        _actions = ObservedObject(wrappedValue: session.actions)
        _route = State(initialValue: session.route)
        _showSettings = State(initialValue: session.showSettings)
        settingsAlreadyOpen = session.showSettings
    }

    var body: some View {
        GeometryReader { geo in
            ZStack(alignment: .topLeading) {
                mainLayout
                panelLayer
                floatingSidebar
                if showSettings {
                    SettingsSheet(browser: browser, isPresented: $showSettings,
                                  initialCategory: session.settingsCategory,
                                  appearsInstantly: settingsAlreadyOpen,
                                  onCategoryChange: { session.settingsCategory = $0 })
                        .transition(.opacity)
                        .zIndex(20)
                }
            }
            .coordinateSpace(name: PanelHost.space)
            .background(AlternateShortcuts(perform: handle))
            .background(WindowReader { found in
                window = found
                edge.window = found
            })
            .focusedSceneObject(actions)
            .onAppear {
                hostSize = geo.size
                actions.perform = { handle($0) }
                edge.isEnabled = !pinned
                edge.start()
                takeLinks()
            }
            .onChange(of: geo.size) { _, size in
                hostSize = size
                // Shrinking the window drags the panels back inside with it,
                // rather than stranding them past the edge where the title bar
                // can no longer be grabbed.
                panels.clampAll(in: size)
            }
            .onChange(of: pinned) { _, isPinned in edge.isEnabled = !isPinned }
            .onChange(of: route) { _, now in session.route = now }
            .onChange(of: showSettings) { _, now in session.showSettings = now }
            .onChange(of: inbox.pending) { _, _ in takeLinks() }
            .onChange(of: browser.shortcutSettingsRequested) { _, requested in
                guard requested else { return }
                browser.shortcutSettingsRequested = false
                session.settingsCategory = .newTab
                withAnimation(Motion.surface) { showSettings = true }
            }

        }
        .ignoresSafeArea()
    }

    // MARK: Layout

    private var mainLayout: some View {
        HStack(spacing: 0) {
            if pinned {
                sidebar(floating: false)
                    .padding([.leading, .top, .bottom], Sidebar.inset)
                    .transition(.move(edge: .leading).combined(with: .opacity))
            }
            page
                .frame(maxWidth: .infinity, maxHeight: .infinity)
                .environment(\.sidebarHidden, !pinned)
        }
        .background(Palette.background)
    }

    /// Low-profile mode: the same sidebar, sliding out over the content when
    /// the cursor reaches the window's leading edge.
    @ViewBuilder
    private var floatingSidebar: some View {
        if !pinned && edge.revealed {
            sidebar(floating: true)
                .padding(Sidebar.inset)
                .frame(maxHeight: .infinity)
                .transition(.asymmetric(
                    insertion: .move(edge: .leading).combined(with: .opacity),
                    removal: .move(edge: .leading).combined(with: .opacity)))
                .zIndex(10)
        }
    }

    private func sidebar(floating: Bool) -> some View {
        Sidebar(
            route: Binding(get: { route }, set: { go($0) }),
            pinned: $pinned,
            recents: recents,
            panels: panels,
            floating: floating,
            onOpen: open,
            onReselect: { target in
                // Clicking the page you're already on returns it to its
                // starting state, the way a tab bar does.
                if target == .toolbox { toolboxResetToken += 1 }
            },
            onRestore: { panel in
                let restored = withAnimation(Motion.surface) {
                    panels.restore(panel.id, in: hostSize)
                }
                if !restored { NSSound.beep() }
            },
            onSettings: { withAnimation(Motion.surface) { showSettings = true } },
            onDropFiles: { item, urls in
                guard let first = urls.first else { return }
                deliver(first, to: item)
                open(item)
            },
            onTearOff: tearOff,
            onMinimizeTarget: { minimizeTarget = $0 }
        )
    }

    @ViewBuilder
    private var page: some View {
        ZStack {
            switch route {
            case .browser:
                BrowserView(model: browser)
                    .transition(pageTransition)
            case .toolbox:
                ToolboxView(onOpen: open, onTearOff: tearOff, focusToken: toolboxFocusToken)
                    .id(toolboxResetToken)
                    .overlay(alignment: .top) {
                        WindowDragArea().frame(height: 30)
                    }
                    .transition(pageTransition)
            case .feature(let item):
                FeatureHost(
                    item: item,
                    onBack: { go(.toolbox, back: true) },
                    onPopOut: {
                        // The page hands itself to a window and the main area
                        // steps back to the Toolbox, so the same feature isn't
                        // on screen twice.
                        if panels.open(item, at: CGPoint(x: hostSize.width * 0.6, y: 90),
                                       in: hostSize) {
                            go(.toolbox, back: true)
                        } else {
                            NSSound.beep()
                        }
                    }
                ) {
                    featureBody(item)
                }
                .id(item)
                .transition(pageTransition)
            }
        }
        .animation(reduceMotion ? .easeOut(duration: 0.15) : Motion.standard, value: route)
    }

    /// Forward navigation slides in from the right, back from the left — a
    /// short distance with a crossfade, which reads as moving between pages
    /// without the whole screen sweeping past.
    private var pageTransition: AnyTransition {
        guard !reduceMotion else { return .opacity }
        return .asymmetric(
            insertion: .offset(x: navigatingBack ? -22 : 22).combined(with: .opacity),
            removal: .opacity)
    }

    // MARK: Panels

    /// The floating panels, drawn above the page.
    @ViewBuilder
    private var panelLayer: some View {
        ForEach(panels.panels) { panel in
            FloatingPanelView(
                panel: panel,
                isFront: panels.frontID == panel.id,
                bounds: hostSize,
                minimizeTarget: pinned ? minimizeTarget : CGPoint(x: 0, y: minimizeTarget.y),
                store: panels,
                onClose: { withAnimation(Motion.standard) { panels.close(panel.id) } },
                onMinimize: {
                    withAnimation(reduceMotion ? .easeOut(duration: 0.15) : Motion.surface) {
                        panels.minimize(panel.id)
                    }
                    // Tucked away, the sidebar peeks out so you see where the
                    // window went.
                    if !pinned { edge.peek() }
                }
            ) {
                featureBody(panel.content)
                    // Every tool reads this to tighten its layout.
                    .environment(\.inFloatingPanel, true)
            }
            .transition(.asymmetric(
                insertion: .scale(scale: 0.92).combined(with: .opacity),
                removal: .scale(scale: 0.96).combined(with: .opacity)))
            .zIndex(panel.isMinimized ? 0 : 1)
        }
    }

    /// One feature's view — the same view whether it's a page or a window.
    @ViewBuilder
    private func featureBody(_ item: PanelContent) -> some View {
        switch item {
        case .section(.viewer):
            FileViewerView(model: viewer, pendingFile: $pendingPreview,
                           onConvert: { url in
                               pendingConvert = url
                               open(.section(.convert))
                           })
        case .section(.convert):
            ConvertView(model: converter, pendingFile: $pendingConvert,
                        onPreview: { url in
                            pendingPreview = url
                            open(.section(.viewer))
                        })
        case .section(.download): DownloadView()
        case .section(.unblock): UnblockView()
        case .section(.diagnose): DiagnoseView()
        case .section(.network): NetworkView()
        case .section(.security): SecurityView()
        case .section: EmptyView()
        case .tool(let tool): ToolDetailView(tool: tool, backgroundRemover: backgroundRemover)
        }
    }

    // MARK: Links from other apps

    /// Links macOS handed this window — Krackn as the default browser, or
    /// "Open With". Web links open as a new tab here; files go to the viewer,
    /// except web files, which a browser should show.
    private func takeLinks() {
        let links = inbox.pending
        guard !links.isEmpty else { return }
        inbox.pending = []
        var openedTab = false
        for url in links {
            let webFile = ["html", "htm", "xhtml", "svg", "webarchive"]
                .contains(url.pathExtension.lowercased())
            if url.isFileURL && !webFile {
                pendingPreview = url
                open(.section(.viewer))
            } else {
                browser.newTab(url: url.absoluteString)
                openedTab = true
            }
        }
        if openedTab { go(.browser) }
        // Bring this window forward, as Safari does when it's handed a link.
        window?.makeKeyAndOrderFront(nil)
    }

    // MARK: Commands

    /// The menu bar's commands, for this window.
    private func handle(_ command: AppCommand) {
        // Commands that are about the browser bring it forward first, so ⌘T
        // or ⌘L from the Toolbox does what it says rather than nothing.
        func inBrowser(_ action: () -> Void) {
            if route != .browser { go(.browser) }
            action()
        }
        let tab = browser.active
        switch command {
        case .newTab:
            inBrowser { browser.newTab(url: StartPage.url.absoluteString) }
        case .openLocation:
            inBrowser { browser.addressFocusRequested = true }
        case .closeTab:
            // A popped-out window in front closes first, the way ⌘W closes
            // the frontmost thing.
            if let front = panels.frontID, panels.visible.contains(where: { $0.id == front }) {
                withAnimation(Motion.standard) { panels.close(front) }
            } else if route == .browser, let tab {
                withAnimation(Motion.standard) { browser.close(tab) }
            }
        case .closeWindow:
            window?.performClose(nil)
        case .reopenClosed:
            inBrowser {
                if !browser.reopenClosedTab(), let records = BrowserModel.closedWindows.pop() {
                    BrowserModel.pendingWindowTabs = records
                    openWindow(id: "main")
                }
            }
        case .print:
            if route == .browser { tab?.printPage() }
        case .find:
            switch route {
            case .browser: browser.showFind()
            case .toolbox: toolboxFocusToken += 1
            case .feature: break
            }
        case .findNext: if route == .browser { browser.findNext() }
        case .findPrevious: if route == .browser { browser.findNext(backwards: true) }
        case .toggleSidebar:
            withAnimation(Motion.surface) { pinned.toggle() }
        case .favouritesBar:
            inBrowser {
                let key = AppSettings.Key.showFavouritesBar
                UserDefaults.standard.set(!AppSettings.flag(key, default: true), forKey: key)
            }
        case .bookmarks: inBrowser { browser.toggleLibrary(.bookmarks) }
        case .readingList: inBrowser { browser.toggleLibrary(.readingList) }
        case .tabOverview: inBrowser { browser.toggleOverview() }
        case .reader: inBrowser { browser.toggleReader() }
        case .reload: if route == .browser { tab?.reload() }
        case .stop: if route == .browser { tab?.stop() }
        case .zoomIn: if route == .browser { tab?.zoomIn() }
        case .zoomOut: if route == .browser { tab?.zoomOut() }
        case .actualSize: if route == .browser { tab?.resetZoom() }
        case .textBigger: if route == .browser { tab?.makeTextBigger() }
        case .textSmaller: if route == .browser { tab?.makeTextSmaller() }
        case .back:
            switch route {
            case .browser: tab?.goBack()
            case .feature: go(.toolbox, back: true)
            case .toolbox: break
            }
        case .forward: if route == .browser { tab?.goForward() }
        case .home: inBrowser { tab?.goHome() }
        case .addBookmark: if route == .browser { browser.addBookmark() }
        case .addToReadingList: if route == .browser { browser.addToReadingList() }
        case .nextTab: inBrowser { browser.selectNextTab() }
        case .previousTab: inBrowser { browser.selectPreviousTab() }
        case .selectTab(let n): inBrowser { browser.selectTab(number: n) }
        case .settings:
            withAnimation(Motion.surface) { showSettings = true }
        }
    }

    // MARK: Actions

    private func go(_ target: Route, back: Bool = false) {
        navigatingBack = back
        route = target
    }

    private func open(_ item: PanelContent) {
        recents.touch(item)
        // Already open in a window: bring that forward rather than showing the
        // feature twice.
        if let panel = panels.panels.first(where: { $0.content == item }) {
            withAnimation(Motion.surface) { _ = panels.restore(panel.id, in: hostSize) }
            return
        }
        go(.feature(item))
    }

    private func tearOff(_ item: PanelContent, at point: CGPoint) {
        recents.touch(item)
        let opened = withAnimation(Motion.surface) {
            panels.open(item, at: point, in: hostSize)
        }
        // Silently doing nothing at the cap would read as a broken gesture.
        if !opened { NSSound.beep(); return }
        // The page it came from steps back, so it isn't on screen twice.
        if route == .feature(item) { go(.toolbox, back: true) }
    }

    private func deliver(_ file: URL, to item: PanelContent) {
        switch item {
        case .section(.viewer): pendingPreview = file
        case .section(.convert): pendingConvert = file
        case .tool(.bgRemove):
            backgroundRemover.open(file)
            Task { await backgroundRemover.run() }
        default: break
        }
    }
}

/// A feature shown in the main area: a slim top bar — back to the Toolbox,
/// the feature's name, and a button to pop it out into a window — over the
/// feature itself. Every feature gets exactly this frame, which is what makes
/// Download, Open a Link and Diagnose read as one app instead of three.
struct FeatureHost<Content: View>: View {
    let item: PanelContent
    let onBack: () -> Void
    let onPopOut: () -> Void
    @ViewBuilder var content: Content
    @Environment(\.sidebarHidden) private var sidebarHidden

    var body: some View {
        VStack(spacing: 0) {
            HStack(spacing: 4) {
                if sidebarHidden {
                    Color.clear.frame(width: WindowMetrics.trafficLightsWidth - 14, height: 1)
                }
                BarButton(system: "chevron.left", help: "Back to Toolbox (⌘[)", action: onBack)
                HStack(spacing: 7) {
                    Image(systemName: item.icon)
                        .font(.system(size: 13, weight: .regular))
                        .foregroundStyle(Palette.accentText)
                    Text(item.title)
                        .font(.app(14, .semibold))
                        .foregroundStyle(Palette.text)
                        .lineLimit(1)
                }
                .padding(.leading, 4)
                WindowDragArea().frame(maxWidth: .infinity).frame(height: 44)
                if item.isPoppable {
                    BarButton(system: "macwindow.badge.plus", help: "Open in Window",
                              action: onPopOut)
                }
            }
            .padding(.horizontal, 12)
            .frame(height: 50)

            content
                .environment(\.hostedUnderBar, true)
                .frame(maxWidth: .infinity, maxHeight: .infinity)
        }
    }
}

extension PanelContent {
    var isPoppable: Bool {
        switch self {
        case .section(let s): return PanelContent.isTearable(s)
        case .tool: return true
        }
    }
}

/// A toolbar glyph: borderless, a soft rounded fill under the pointer, a
/// slight settle on press.
struct BarButton: View {
    let system: String
    let help: String
    let action: () -> Void
    @State private var hovering = false

    var body: some View {
        Button(action: action) {
            Image(systemName: system)
                .font(.system(size: 13.5, weight: .regular))
                .foregroundStyle(Palette.text.opacity(hovering ? 0.9 : 0.65))
                .frame(width: 30, height: 28)
                .background(RoundedRectangle(cornerRadius: 7, style: .continuous)
                    .fill(hovering ? Palette.controlFill : .clear))
                .contentShape(Rectangle())
        }
        .buttonStyle(PressSettleStyle())
        .onHover { hovering = $0 }
        .animation(Motion.hover, value: hovering)
        .help(help)
    }
}

/// A press that settles the control a fraction smaller — felt more than seen.
struct PressSettleStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .scaleEffect(configuration.isPressed ? 0.94 : 1)
            .animation(Motion.quick, value: configuration.isPressed)
    }
}

/// Links handed to this window by macOS, waiting for the browser to take
/// them — they can arrive before sign-in finishes, or at launch before the
/// browser exists.
@MainActor
final class LinkInbox: ObservableObject {
    @Published var pending: [URL] = []
}

/// Shows the sign-in screen until there's a session, then the app.
struct AppGate: View {
    @Binding var themeRaw: String
    let session: WindowSession
    @ObservedObject private var account = AccountStore.shared
    private var inbox: LinkInbox { session.inbox }

    var body: some View {
        Group {
            if account.isSignedIn {
                RootView(themeRaw: $themeRaw, session: session)
            } else {
                AuthView()
            }
        }
        .animation(.easeInOut(duration: 0.25), value: account.isSignedIn)
        // Claim links from other apps for *this* window. Without this SwiftUI
        // makes a new window for each one; with it they open as tabs here.
        // Measured with a probe app: before, two links made three windows;
        // after, one window took both — and a link that launches the app
        // opens in its first window.
        .handlesExternalEvents(preferring: ["*"], allowing: ["*"])
        .onOpenURL { inbox.pending.append($0) }
    }
}

// MARK: - Shared chrome

/// A page title. Only drawn where nothing else names the page: under a
/// feature's top bar or in a popped-out window's title bar it would be the
/// same word a second time, larger, so it steps aside.
struct PageHeader: View {
    let title: String
    var subtitle: String?
    @Environment(\.inFloatingPanel) private var inPanel
    @Environment(\.hostedUnderBar) private var underBar

    init(title: String, subtitle: String? = nil) {
        self.title = title
        self.subtitle = subtitle
    }

    var body: some View {
        if !inPanel && !underBar {
            VStack(alignment: .leading, spacing: 4) {
                Text(title)
                    .font(.app(26, .semibold))
                    .tracking(-0.4)
                    .foregroundStyle(Palette.text)
                    .lineLimit(1)
                    .minimumScaleFactor(0.7)
                if let subtitle {
                    Text(subtitle)
                        .font(.app(13))
                        .foregroundStyle(Palette.textSecondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(.bottom, 4)
        }
    }
}

/// Centres a section's content in a fixed-width column with a generous top
/// margin, the way a web page lays out rather than an edge-to-edge form.
struct PageContainer<Content: View>: View {
    @ViewBuilder var content: Content
    @Environment(\.inFloatingPanel) private var inPanel
    @Environment(\.hostedUnderBar) private var underBar

    private var topPadding: CGFloat {
        if inPanel { return 14 }
        // Under a feature's top bar the traffic lights are already cleared.
        return underBar ? 10 : Self.trafficLightInset + 22
    }

    var body: some View {
        // Frosted fades at both edges, so content dissolves into the chrome as
        // it scrolls out rather than being sliced off by a hard boundary. Only
        // the edges that actually have content beyond them get one.
        FadingScrollView(fadeHeight: inPanel ? 24 : 40) {
            VStack(alignment: .leading, spacing: inPanel ? 14 : 22) {
                content
            }
            .frame(maxWidth: 720, alignment: .leading)
            .frame(maxWidth: .infinity, alignment: .center)
            .padding(.horizontal, inPanel ? 16 : 32)
            .padding(.top, topPadding)
            .padding(.bottom, inPanel ? 24 : 48)
        }
        .frame(maxWidth: .infinity)
    }
}

/// A short, non-dismissable note. Used for the honesty callouts — what a
/// feature can't do, what a tool needs installed.
///
/// A faint tint and a coloured glyph, no border: a callout should be noticed
/// without framing itself like an alert.
struct InfoBanner: View {
    enum Kind {
        case info, warning, success

        var color: Color {
            switch self {
            case .info: return Palette.textSecondary
            case .warning: return Palette.caution
            case .success: return Palette.positive
            }
        }

        var icon: String {
            switch self {
            case .info: return "info.circle"
            case .warning: return "exclamationmark.triangle.fill"
            case .success: return "checkmark.circle.fill"
            }
        }
    }

    let kind: Kind
    let text: String

    var body: some View {
        HStack(alignment: .firstTextBaseline, spacing: 8) {
            Image(systemName: kind.icon)
                .font(.system(size: 12, weight: .medium))
                .foregroundStyle(kind.color)
            Text(.init(text))
                .font(.app(12.5))
                .foregroundStyle(Palette.text.opacity(0.85))
                .lineSpacing(1.5)
                .fixedSize(horizontal: false, vertical: true)
                .textSelection(.enabled)
            Spacer(minLength: 0)
        }
        .padding(.vertical, 10)
        .padding(.horizontal, 12)
        .background(
            (kind == .info ? Palette.controlFill : kind.color.opacity(0.10)),
            in: RoundedRectangle(cornerRadius: Metrics.cardRadius - 2, style: .continuous)
        )
    }
}

/// A titled group, laid out the way System Settings groups controls: a small
/// secondary heading sitting *above* the card, and the controls inside it.
/// Putting the heading inside the card at title weight made every page a stack
/// of competing headlines.
struct SectionCard<Content: View>: View {
    let title: String
    @ViewBuilder var content: Content
    @Environment(\.inFloatingPanel) private var inPanel

    var body: some View {
        VStack(alignment: .leading, spacing: Metrics.sectionHeaderGap) {
            if !title.isEmpty {
                Text(title)
                    .font(.app(inPanel ? 11.5 : 12.5, .semibold))
                    .foregroundStyle(Palette.textSecondary)
                    .padding(.leading, 4)
            }
            VStack(alignment: .leading, spacing: inPanel ? 9 : 12) {
                content
            }
            .padding(inPanel ? 11 : 15)
            .frame(maxWidth: .infinity, alignment: .leading)
            .cardSurface()
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }
}

extension View {
    /// Copies text to the pasteboard — used by roughly every utility here.
    func copyToPasteboard(_ text: String) {
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(text, forType: .string)
    }
}

/// Borderless copy affordance with a brief confirmation, like the copy glyph
/// in Xcode or Notes rather than a full button.
struct CopyButton: View {
    let text: String
    @State private var copied = false
    @State private var hovering = false

    var body: some View {
        Button {
            NSPasteboard.general.clearContents()
            NSPasteboard.general.setString(text, forType: .string)
            withAnimation(.easeOut(duration: 0.15)) { copied = true }
            Task {
                try? await Task.sleep(for: .seconds(1.4))
                withAnimation(.easeOut(duration: 0.15)) { copied = false }
            }
        } label: {
            HStack(spacing: 4) {
                Image(systemName: copied ? "checkmark" : "doc.on.doc")
                    .font(.system(size: 11, weight: .medium))
                Text(copied ? "Copied" : "Copy")
                    .font(.app(12, .medium))
            }
            .foregroundStyle(copied ? Palette.positive : Palette.textSecondary)
            .padding(.vertical, 4)
            .padding(.horizontal, 8)
            .background(hovering ? Palette.controlFill : .clear,
                        in: RoundedRectangle(cornerRadius: 6, style: .continuous))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .onHover { hovering = $0 }
        .disabled(text.isEmpty)
        .opacity(text.isEmpty ? 0.4 : 1)
    }
}
