import SwiftUI
import WebKit

struct BrowserView: View {
    /// Owned by RootView, not here. A `@StateObject` would be torn down every
    /// time you navigate away, taking every tab and web view with it — so the
    /// browser has to outlive this view.
    @ObservedObject var model: BrowserModel
    @State private var addressText = ""
    @State private var addressFocused = false
    /// True while the user is editing, so the live URL doesn't overwrite what
    /// they're typing. This is the whole trick to an address bar that always
    /// shows the loaded page *and* lets you type into it.
    @State private var addressEditing = false
    @Environment(\.colorScheme) private var colorScheme
    @Environment(\.sidebarHidden) private var sidebarHidden
    /// Live tab-drag state. `dragOffset` moves the chip under the cursor;
    /// `frames` is how we work out which slot it's over.
    @State private var draggingID: UUID?
    @State private var dragOffset: CGFloat = 0
    /// The chip's centre when the drag began, and where its slot currently
    /// rests — the drawn position is the first plus the drag distance.
    @State private var dragStartCenter: CGFloat = 0
    @State private var dragLayoutCenter: CGFloat = 0
    /// Set right after a swap, until the strip has reported fresh frames.
    @State private var framesStale = false
    @State private var dragVisual: CGFloat = 0
    @State private var tabFrames: [UUID: CGRect] = [:]
    @AppStorage(AppSettings.Key.searchEngine) private var searchEngine = "Google"
    @AppStorage(AppSettings.Key.tabOrganization) private var organization = "manual"
    @AppStorage(AppSettings.Key.tabDropdowns) private var tabDropdowns = false
    /// Which stack's dropdown is open, and where the stacks sit.
    /// Dropdown style: the slot being dragged — "pin:<id>" for a pinned tab,
    /// else a stack's key. Shares the offset/centre state with tab drags.
    @State private var draggingSlot: String?
    @State private var openStack: String?
    @State private var stackFrames: [String: CGRect] = [:]
    /// Spring-loading: a drag opened the dropdown, so the drag leaving both
    /// the stack and the list closes it again.
    @State private var dragOpened = false
    @State private var dragOverStack = false
    @State private var dragOverDropdown = false
    @State private var dragClose: Task<Void, Never>?
    @AppStorage(AppSettings.Key.showFavouritesBar) private var favouritesBarEnabled = true
    @ObservedObject private var library = LibraryStore.shared
    /// Only when there's something on it: an empty bar is a strip of dead
    /// space between the toolbar and the tabs. The first ⌘D brings it in.
    private var showFavouritesBar: Bool {
        favouritesBarEnabled && !library.library.favourites.isEmpty
    }

    var body: some View {
        VStack(spacing: 0) {
            // Every gap in the bar is an explicit drag strip. A
            // `.background(WindowDragArea())` looks equivalent but never
            // receives clicks: SwiftUI only routes hits to a representable
            // that occupies real layout space, so the padding has to *be* the
            // drag view rather than sit behind it.
            // Every band here has an explicit whole-number height. Left to
            // intrinsic sizing, the tab strip's height comes from text metrics
            // and can be fractional — which puts the web view below it on a
            // half-pixel boundary and gets its whole layer resampled.
            // Safari's order: toolbar, Favourites bar, then the tab bar along
            // the bottom of the chrome, spanning the window.
            //
            // The tab row is exactly a tab tall, so the gaps are what the
            // spacers say: 5pt between the address bar and the tabs (2 inside
            // the toolbar row + 3), and 5pt between the tabs and the page
            // (3 + the 2pt loading-bar lane below, which is part of the header).
            VStack(spacing: 0) {
                WindowDragArea().frame(height: 6)
                toolbar.frame(height: 34)
                WindowDragArea().frame(height: 3)
                if showFavouritesBar {
                    FavouritesBar(model: model).frame(height: 26)
                        .transition(.opacity)
                }
                tabStrip.frame(height: 28)
                WindowDragArea().frame(height: 3)
            }
            .frame(height: showFavouritesBar ? 100 : 74)
            .background(Palette.tint)
            .clipped()
            if model.findVisible {
                FindBar(model: model)
                    .frame(height: 36)
                    .transition(.move(edge: .top).combined(with: .opacity))
            }
            // Always present, so the content below never shifts by 2pt when a
            // load starts or ends. A layout that moves under the web view is
            // another way to land it off the pixel grid.
            // Header-coloured, so it reads as the header's bottom edge and the
            // gap under the tabs is the 5pt the layout above intends.
            ZStack(alignment: .leading) {
                Rectangle().fill(Palette.tint)
                if let tab = model.active, tab.isLoading, tab.progress < 1 {
                    GeometryReader { geo in
                        Rectangle()
                            .fill(Palette.accent)
                            .frame(width: geo.size.width * tab.progress)
                            .animation(.easeOut(duration: 0.2), value: tab.progress)
                    }
                }
            }
            .frame(height: 2)
            HStack(spacing: 0) {
                if model.libraryPane != nil {
                    LibrarySidebar(model: model)
                        .transition(.move(edge: .leading).combined(with: .opacity))
                }
                content
            }
        }
        .background(Palette.background)
        .coordinateSpace(name: BrowserSpace.name)
        .onPreferenceChange(StackFrameKey.self) { frames in
            stackFrames = frames
            // Fresh frames after a swap: re-anchor on the real resting place.
            if framesStale, let key = draggingSlot, let frame = frames[key] {
                dragLayoutCenter = frame.midX
                dragOffset = dragVisual - frame.midX
                framesStale = false
            }
        }
        .overlay(alignment: .topLeading) { stackDropdown }
        .overlay {
            if model.showsOverview {
                TabOverview(model: model)
                    .transition(.opacity.combined(with: .scale(scale: 1.03)))
            }
        }
        .animation(Motion.surface, value: showFavouritesBar)
        .animation(Motion.quick, value: model.findVisible)
        .onChange(of: model.activeID) { _, _ in syncAddress() }
        .onAppear { syncAddress(); applyAppearance(); focusStartPageIfNeeded() }
        .onChange(of: model.activeIsStartPage) { _, _ in focusStartPageIfNeeded() }
        // The start page picks its palette from prefers-color-scheme, which
        // follows the web view's appearance — not the system's. Pinning it here
        // is what makes "Light" stay light while macOS is dark.
        .onChange(of: colorScheme) { _, _ in applyAppearance() }
        .onChange(of: searchEngine) { _, _ in
            model.startPageHandler.searchURL = AppSettings.searchEngine.formAction
            model.reloadStartPages()
        }
        .onChange(of: tabDropdowns) { _, _ in openStack = nil }
        .onChange(of: openStack) { _, open in if open == nil { dragOpened = false } }
        .onChange(of: organization) { _, _ in
            withAnimation(.spring(response: 0.32, dampingFraction: 0.85)) {
                model.reorganize()
            }
        }
    }

    // MARK: - Tabs

    /// The tab bar: the whole width of the window, shared between the tabs —
    /// pinned icons at the left, then every other tab an equal slice, shrinking
    /// to icons when there are too many to read (see `TabBarLayout`).
    private var tabStrip: some View {
        GeometryReader { geo in
            if tabDropdowns {
                stackedRow(width: geo.size.width)
            } else {
                tabRow(width: geo.size.width)
            }
        }
        .padding(.horizontal, 8)
        .coordinateSpace(name: TabReorder.space)
        .onPreferenceChange(TabReorder.FramePreference.self) { frames in
            tabFrames = frames
            // Fresh frames after a swap: re-anchor on the real resting place.
            if framesStale, let id = draggingID, let frame = frames[id] {
                dragLayoutCenter = frame.midX
                dragOffset = dragVisual - frame.midX
                framesStale = false
            }
        }
    }

    /// The Dropdown tab style: one slot per site, in the arrangement's order. A
    /// site with one page is an ordinary tab; a site with several is a stack —
    /// click it to pick the page.
    private func stackedRow(width: CGFloat) -> some View {
        let pinnedTabs = model.tabs.filter(\.isPinned)
        let stacks = TabOrganizer.stacks(model.tabs.map { $0.snapshot(isActive: $0.id == model.activeID) },
                                         alphabetical: organization == TabOrganization.alphabetical.rawValue)
        let activeStack = stacks.firstIndex { stack in stack.ids.contains { $0 == model.activeID } }
        let layout = TabBarLayout.layout(total: width, pinned: pinnedTabs.count,
                                         loose: stacks.count, activeLoose: activeStack)
        let lookup = Dictionary(uniqueKeysWithValues: model.tabs.map { ($0.id, $0) })
        return HStack(spacing: 2) {
            ForEach(pinnedTabs) { tab in
                let key = "pin:" + tab.id.uuidString
                plainChip(tab, width: TabBarLayout.pinnedWidth, compact: false, divider: false)
                    .draggableSlot(key: key, dragging: draggingSlot, offset: dragOffset,
                                   gesture: slotDrag(key: key, pinned: true))
            }
            if !pinnedTabs.isEmpty && !stacks.isEmpty {
                Rectangle().fill(Palette.separator)
                    .frame(width: 1, height: 16)
                    .frame(width: TabBarLayout.dividerWidth - 2)
            }
            ForEach(Array(stacks.enumerated()), id: \.element.key) { index, stack in
                let tabs = stack.ids.compactMap { lookup[$0] }
                let w = layout.looseWidths[safe: index] ?? 120
                let compact = layout.compact[safe: index] ?? false
                let divider = index != activeStack && index + 1 != activeStack && index < stacks.count - 1
                Group {
                    if tabs.count == 1, let tab = tabs.first {
                        plainChip(tab, width: w, compact: compact, divider: divider)
                    } else if !tabs.isEmpty {
                        TabStackChip(tabs: tabs, domain: stack.name, key: stack.key, model: model,
                                     isActive: index == activeStack, width: w, compact: compact,
                                     showsDivider: divider, isOpen: openStack == stack.key) {
                            withAnimation(Motion.quick) {
                                openStack = openStack == stack.key ? nil : stack.key
                            }
                            dragOpened = false
                        } onDragOpen: {
                            withAnimation(Motion.quick) { openStack = stack.key }
                            dragOpened = true
                        } onDragPresence: { inside in
                            dragOverStack = inside
                            dragPresenceChanged()
                        }
                    }
                }
                .draggableSlot(key: stack.key, dragging: draggingSlot, offset: dragOffset,
                               gesture: slotDrag(key: stack.key, pinned: false))
                .transition(.asymmetric(
                    insertion: .opacity.combined(with: .scale(scale: 0.9, anchor: .leading)),
                    removal: .opacity.combined(with: .scale(scale: 0.9, anchor: .center))))
            }
        }
        .frame(width: width, alignment: .leading)
        .animation(Motion.standard, value: stacks.map(\.ids))
        .animation(Motion.standard, value: model.activeID)
    }

    /// The open stack's dropdown, unfolding from beneath its tab. A click
    /// anywhere else closes it, as a menu does.
    @ViewBuilder
    private var stackDropdown: some View {
        if let key = openStack, let frame = stackFrames[key],
           let stack = TabOrganizer.stacks(model.tabs.map { $0.snapshot(isActive: false) })
               .first(where: { $0.key == key }) {
            let lookup = Dictionary(uniqueKeysWithValues: model.tabs.map { ($0.id, $0) })
            let tabs = stack.ids.compactMap { lookup[$0] }
            GeometryReader { geo in
                ZStack(alignment: .topLeading) {
                    Color.black.opacity(0.001)
                        .contentShape(Rectangle())
                        .onTapGesture { withAnimation(Motion.quick) { openStack = nil } }
                    if tabs.count > 1 {
                        TabStackDropdown(tabs: tabs, domain: stack.name, model: model,
                                         dismiss: {
                            withAnimation(Motion.quick) { openStack = nil }
                        }, onDragPresence: { inside in
                            dragOverDropdown = inside
                            dragPresenceChanged()
                        })
                        // Centred under the stack, kept inside the window.
                        .offset(x: min(max(frame.midX - TabStackDropdown.width / 2, 8),
                                       geo.size.width - TabStackDropdown.width - 8),
                                y: frame.maxY + 6)
                        .transition(.asymmetric(
                            insertion: .scale(scale: 0.96, anchor: .top).combined(with: .opacity)
                                .combined(with: .offset(y: -4)),
                            removal: .opacity))
                    }
                }
            }
            .transition(.opacity)
        }
    }

    /// A drag that opened a dropdown and then left both the stack and the
    /// list closes it — after a beat, since crossing the gap between them
    /// briefly counts as "neither".
    private func dragPresenceChanged() {
        dragClose?.cancel()
        guard dragOpened, openStack != nil, !dragOverStack, !dragOverDropdown else { return }
        dragClose = Task {
            try? await Task.sleep(for: .milliseconds(300))
            guard !Task.isCancelled else { return }
            await MainActor.run {
                guard dragOpened, !dragOverStack, !dragOverDropdown else { return }
                withAnimation(Motion.quick) { openStack = nil }
                dragOpened = false
            }
        }
    }

    /// Dropdown style, drag-to-reorder: a pinned tab moves among the pinned
    /// tabs, a stack moves along the strip with every page in it. Same
    /// geometry as a tab drag (`dragGesture`): the slot is drawn at its start
    /// plus the drag distance, and its place is judged from where it's drawn.
    private func slotDrag(key: String, pinned: Bool) -> some Gesture {
        DragGesture(minimumDistance: 4, coordinateSpace: .named(BrowserSpace.name))
            .onChanged { value in
                if draggingSlot != key {
                    guard let frame = stackFrames[key] else { return }
                    withAnimation(Motion.quick) { openStack = nil }
                    // A sorted strip has no place for a hand-placed slot:
                    // freeze what's on screen as the manual order, then switch.
                    if model.organization.dragSwitchesToManual {
                        model.freezeDisplayedOrder()
                        organization = TabOrganization.manual.rawValue
                        model.post("Tabs are now in manual order. Change it in Settings › Tabs.")
                    }
                    draggingSlot = key
                    model.isDraggingTab = true
                    dragStartCenter = frame.midX
                    dragLayoutCenter = frame.midX
                    framesStale = false
                }
                let visual = dragStartCenter + value.translation.width
                dragVisual = visual
                dragOffset = visual - dragLayoutCenter

                // Frames lag one layout pass behind a swap; wait for them.
                guard !framesStale else { return }
                let keys = pinned
                    ? model.tabs.filter(\.isPinned).map { "pin:" + $0.id.uuidString }
                    : model.displayedStacks.map(\.key)
                let frames = keys.compactMap { stackFrames[$0] }
                guard frames.count == keys.count, let current = keys.firstIndex(of: key) else { return }
                let target = TabReorder.targetIndex(dragging: current, visualCenterX: visual, frames: frames)
                guard target != current else { return }
                withAnimation(Motion.quick) {
                    if pinned {
                        model.moveTab(from: current, to: target)   // pinned lead the list
                    } else {
                        model.arrange(stacks: TabOrganizer.movingStack(
                            model.displayedStacks.map(\.ids), from: current, to: target))
                    }
                }
                dragLayoutCenter = TabReorder.centerAfterMove(from: current, to: target, frames: frames)
                dragOffset = visual - dragLayoutCenter
                framesStale = true
            }
            .onEnded { _ in
                withAnimation(Motion.standard) {
                    dragOffset = 0
                    draggingSlot = nil
                }
                model.isDraggingTab = false
                model.scheduleSessionSave()
            }
    }

    /// A tab chip with the strip's standard actions, for the stacked row.
    private func plainChip(_ tab: BrowserTab, width: CGFloat, compact: Bool, divider: Bool,
                           isDragging: Bool = false) -> some View {
        TabChip(
            tab: tab, isActive: tab.id == model.activeID, width: width, compact: compact,
            showsDivider: divider, isDragging: isDragging,
            onSelect: { model.activate(tab) },
            onClose: {
                if NSEvent.modifierFlags.contains(.option) {
                    withAnimation(Motion.standard) { model.closeOthers(keeping: tab) }
                } else {
                    withAnimation(Motion.standard) { model.close(tab) }
                }
            },
            onTogglePin: { model.togglePin(tab) },
            onDropFile: { model.activate(tab) },
            model: model)
    }

    private func tabRow(width: CGFloat) -> some View {
            let pinned = model.pinnedCount
            let loose = model.tabs.count - pinned
            let activeLoose = model.tabs.firstIndex { $0.id == model.activeID }
                .flatMap { $0 >= pinned ? $0 - pinned : nil }
            let layout = TabBarLayout.layout(total: width, pinned: pinned,
                                             loose: loose, activeLoose: activeLoose)
            return HStack(spacing: 2) {
                ForEach(Array(model.tabs.enumerated()), id: \.element.id) { index, tab in
                    if index == pinned, pinned > 0 {
                        Rectangle().fill(Palette.separator)
                            .frame(width: 1, height: 16)
                            .frame(width: TabBarLayout.dividerWidth - 2)
                            .transition(.opacity)
                    }
                    let looseIndex = index - pinned
                    let isActive = tab.id == model.activeID
                    let nextIsActive = model.tabs.indices.contains(index + 1)
                        && model.tabs[index + 1].id == model.activeID
                    TabChip(
                        tab: tab,
                        isActive: isActive,
                        width: tab.isPinned ? TabBarLayout.pinnedWidth
                                            : layout.looseWidths[safe: looseIndex] ?? 120,
                        compact: !tab.isPinned && (layout.compact[safe: looseIndex] ?? false),
                        showsDivider: !tab.isPinned && !isActive && !nextIsActive
                            && index < model.tabs.count - 1,
                        isDragging: draggingID == tab.id,
                        onSelect: { model.activate(tab) },
                        onClose: {
                            // ⌥-click: close every other tab, keep this one.
                            if NSEvent.modifierFlags.contains(.option) {
                                withAnimation(Motion.standard) { model.closeOthers(keeping: tab) }
                            } else {
                                withAnimation(Motion.standard) { model.close(tab) }
                            }
                        },
                        onTogglePin: { model.togglePin(tab) },
                        onDropFile: { model.activate(tab) },
                        model: model
                    )
                    .reportsTabFrame(id: tab.id)
                    // Opening and closing read as the tab growing out of, or
                    // shrinking into, its slot while the others close ranks.
                    .transition(.asymmetric(
                        insertion: .opacity.combined(with: .scale(scale: 0.9, anchor: .leading)),
                        removal: .opacity.combined(with: .scale(scale: 0.9, anchor: .center))))
                    // The dragged chip rides above its neighbours and follows
                    // the cursor; everything else animates into its new slot.
                    .offset(x: draggingID == tab.id ? dragOffset : 0)
                    .zIndex(draggingID == tab.id ? 1 : 0)
                    .gesture(dragGesture(for: tab))
                }
            }
            .frame(width: width, alignment: .leading)
            .animation(Motion.standard, value: model.tabs.map(\.id))
            .animation(Motion.standard, value: model.activeID)
    }

    /// Drag-to-reorder. `minimumDistance` keeps a plain click working as a
    /// tab selection rather than starting a drag.
    ///
    /// The chip is drawn at *start position + drag distance*, so it stays
    /// glued to the cursor however the strip reflows underneath it; its slot
    /// is decided by where it's drawn, and after each swap its new resting
    /// place is predicted rather than read back from frames that haven't
    /// caught up yet (see `TabReorder.centerAfterMove`).
    private func dragGesture(for tab: BrowserTab) -> some Gesture {
        DragGesture(minimumDistance: 4, coordinateSpace: .named(TabReorder.space))
            .onChanged { value in
                if draggingID != tab.id {
                    guard let frame = tabFrames[tab.id] else { return }
                    // A sorted strip has no place for a hand-placed tab, so
                    // grabbing one switches to manual order — and says so —
                    // instead of letting the tab snap back on release.
                    if model.organization.dragSwitchesToManual {
                        organization = TabOrganization.manual.rawValue
                        model.post("Tabs are now in manual order. Change it in Settings › Tabs.")
                    }
                    draggingID = tab.id
                    model.isDraggingTab = true
                    dragStartCenter = frame.midX
                    dragLayoutCenter = frame.midX
                    framesStale = false
                }
                let visual = dragStartCenter + value.translation.width
                dragVisual = visual
                dragOffset = visual - dragLayoutCenter

                // Frames lag one layout pass behind a swap; wait for them.
                guard !framesStale else { return }
                let ordered = model.tabs.compactMap { tabFrames[$0.id] }
                guard ordered.count == model.tabs.count,
                      let current = model.tabs.firstIndex(where: { $0.id == tab.id })
                else { return }

                let target = TabReorder.targetIndex(dragging: current, visualCenterX: visual,
                                                    frames: ordered)
                guard target != current else { return }
                let before = model.tabs.map(\.id)
                withAnimation(Motion.quick) {
                    model.moveTab(from: current, to: target)
                }
                // Pinned and loose tabs can't cross the divider, so the move
                // may have been refused; only re-anchor if it happened.
                guard model.tabs.map(\.id) != before,
                      let landed = model.tabs.firstIndex(where: { $0.id == tab.id })
                else { return }
                dragLayoutCenter = TabReorder.centerAfterMove(from: current, to: landed,
                                                              frames: ordered)
                dragOffset = visual - dragLayoutCenter
                framesStale = true
            }
            .onEnded { _ in
                withAnimation(Motion.standard) {
                    dragOffset = 0
                    draggingID = nil
                }
                // By site: the drop decides the order sites first appear in,
                // so the tab's whole group follows it there.
                model.isDraggingTab = false
                model.scheduleSessionSave()
            }
    }

    // MARK: - Toolbar

    private var toolbar: some View {
        // Reading through `model.active` alone was the back/forward bug: this
        // view observes the *model*, and `canGoBack` lives on the tab, so the
        // arrows only refreshed when something else happened to redraw the
        // view. Threading the active tab through a child that observes it
        // directly is what makes them track navigation.
        Group {
            if let tab = model.active {
                ToolbarRow(
                    tab: tab,
                    addressText: $addressText,
                    addressFocused: $addressFocused,
                    addressEditing: $addressEditing,
                    onSubmit: {
                        // ⌘Return opens what was typed in a new tab behind
                        // this one; ⇧⌘Return brings that tab to the front.
                        let flags = NSApp.currentEvent?.modifierFlags ?? []
                        if flags.contains(.command) {
                            model.newTab(url: addressText, activate: flags.contains(.shift))
                            addressEditing = false
                            addressText = tab.displayURL
                        } else {
                            tab.go(to: addressText)
                            addressEditing = false
                        }
                    },
                    onCancel: { addressEditing = false; syncAddress() },
                    onHome: { tab.goHome() },
                    model: model)
            } else {
                Color.clear.frame(height: 32)
            }
        }
        .padding(.horizontal, 10)
    }

    // MARK: - Content

    @ViewBuilder
    private var content: some View {
        if let tab = model.active {
            ZStack(alignment: .top) {
                WebViewHost(webView: tab.webView, shielded: openStack != nil)
                    .overlay(alignment: .topLeading) { KeychainKey(tab: tab) }

                if let article = model.reader {
                    ReaderOverlay(article: article) {
                        withAnimation(Motion.standard) { model.reader = nil }
                    }
                    .transition(.opacity.combined(with: .offset(y: 12)))
                }

                VStack(spacing: 8) {
                    if let verdict = tab.securityBlock {
                        SecurityInterstitial(verdict: verdict, tab: tab)
                            .padding(.horizontal, 12)
                    } else if let blocked = tab.blockedURL {
                        blockedBanner(tab: tab, url: blocked)
                    } else if let error = tab.loadError, tab.progress == 0 {
                        InfoBanner(kind: .warning, text: error)
                            .padding(.horizontal, 12)
                    }
                    if let notice = model.notice {
                        InfoBanner(kind: .info, text: notice)
                            .padding(.horizontal, 12)
                    }
                }
                .padding(.top, 8)
            }
        } else {
            Text("No tab open").foregroundStyle(Palette.textSecondary)
        }
    }

    /// The relay is a fallback now, not the main path. It only appears when a
    /// load actually failed in a way that suggests the network is interfering.
    private func blockedBanner(tab: BrowserTab, url: URL) -> some View {
        VStack(alignment: .leading, spacing: 9) {
            Text("Couldn't reach \(url.host ?? url.absoluteString)")
                .font(.app(13, .semibold))
            Text(
                "If this site is blocked here, the reliable fix is **Network & VPN → "
                + "Generate Profile**, which unblocks it everywhere including logins. "
                + "Reading it through the relay works without that, but the page will be "
                + "text-only and you won't be able to sign in."
            )
            .font(.app(11.5))
            .foregroundStyle(Palette.textSecondary)
            .fixedSize(horizontal: false, vertical: true)

            HStack {
                Button("Retry") { tab.reload() }
                    .buttonStyle(.pill)
                Button("Read via relay") {
                    tab.blockedURL = nil
                    // Serve the relayed copy under our scheme in this same tab.
                    if let relayed = RelayURL.encode(url) {
                        tab.webView.load(URLRequest(url: relayed))
                    }
                }
                .buttonStyle(.pillSecondary)
                Button("Dismiss") { tab.blockedURL = nil }
                    .buttonStyle(.pillQuiet)
            }
        }
        .padding(12)
        .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 9))
        .overlay(
            RoundedRectangle(cornerRadius: 9).strokeBorder(Palette.caution.opacity(0.5))
        )
        .padding(.horizontal, 12)
    }

    private func syncAddress() {
        guard !addressEditing else { return }
        addressText = model.active?.displayURL ?? ""
    }

    /// Hands the new-tab page first-responder status so you can start typing
    /// immediately. Only when the start page is what's actually on screen —
    /// stealing focus from a real web page would break in-page typing.
    private func focusStartPageIfNeeded() {
        guard model.activeIsStartPage, let tab = model.active else { return }
        // A beat after layout, otherwise the window hasn't adopted the view yet.
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.12) {
            guard model.activeIsStartPage else { return }
            tab.webView.window?.makeFirstResponder(tab.webView)
            tab.webView.evaluateJavaScript(
                "document.querySelector('input[name=q]')?.focus()", completionHandler: nil)
        }
    }

    private func applyAppearance() {
        let appearance = NSAppearance(named: colorScheme == .dark ? .darkAqua : .aqua)
        for tab in model.tabs {
            tab.webView.appearance = appearance
        }
    }
}

// MARK: - Toolbar row

/// The navigation controls and address bar for one tab.
///
/// Split into its own view purely so it can `@ObservedObject` the tab. The
/// enclosing `BrowserView` observes the browser model, and a change to
/// `tab.canGoBack` doesn't touch the model — which is exactly why the back and
/// forward arrows used to sit stale until some unrelated redraw refreshed them.
private struct ToolbarRow: View {
    @ObservedObject var tab: BrowserTab
    @Binding var addressText: String
    @Binding var addressFocused: Bool
    @Binding var addressEditing: Bool
    let onSubmit: () -> Void
    let onCancel: () -> Void
    let onHome: () -> Void
    @ObservedObject var model: BrowserModel

    /// Pulls the live URL into the bar unless the user is actively typing.
    ///
    /// The editing guard used to latch on permanently: focus was reported from
    /// `resignFirstResponder`, which an NSTextField often never receives
    /// because its field editor holds first-responder status instead. One click
    /// in the address bar and it stopped following the page for good.
    private func syncFromPage() {
        guard !addressEditing else { return }
        let live = tab.displayURL
        if addressText != live { addressText = live }
    }

    @Environment(\.sidebarHidden) private var sidebarHidden

    /// What the bar shows at rest: just the site, as Safari does. The full
    /// address appears — selected, ready to copy or replace — the moment you
    /// click in.
    private var idleText: String {
        guard let url = tab.webView.url else { return "" }
        if url.scheme == StartPage.scheme { return "" }
        if let host = url.host, url.scheme?.hasPrefix("http") == true {
            return host.hasPrefix("www.") ? String(host.dropFirst(4)) : host
        }
        return tab.displayURL
    }

    private var isWebPage: Bool { tab.webView.url?.scheme?.hasPrefix("http") == true }

    var body: some View {
        // The address bar sits in the middle of the window whatever's either
        // side of it — Safari's layout — so it's laid over the row rather than
        // placed in it, held clear of the groups on each side.
        let leading: CGFloat = (sidebarHidden ? WindowMetrics.trafficLightsWidth - 6 : 0) + 66
        let trailing: CGFloat = model.downloads.isEmpty ? 104 : 138
        ZStack {
            HStack(spacing: 6) {
                if sidebarHidden {
                    Color.clear.frame(width: WindowMetrics.trafficLightsWidth - 12, height: 1)
                }
                HStack(spacing: 0) {
                    HistoryButton(tab: tab, forward: false)
                    HistoryButton(tab: tab, forward: true)
                }
                .padding(.horizontal, 2)
                .frame(height: 28)
                .background(Palette.text.opacity(0.05),
                            in: RoundedRectangle(cornerRadius: 8, style: .continuous))

                WindowDragArea().frame(maxWidth: .infinity).frame(height: 32)

                HStack(spacing: 2) {
                    if !model.downloads.isEmpty {
                        DownloadsButton(model: model)
                            .transition(.scale(scale: 0.6).combined(with: .opacity))
                    }
                    if let url = tab.webView.url, isWebPage {
                        ShareLink(item: url) {
                            Image(systemName: "square.and.arrow.up")
                                .font(.system(size: 13.5))
                                .frame(width: 30, height: 28)
                                .contentShape(Rectangle())
                        }
                        .buttonStyle(PressSettleStyle())
                        .foregroundStyle(Palette.text.opacity(0.65))
                        .help("Share")
                    } else {
                        Color.clear.frame(width: 30, height: 28)
                    }
                    BarButton(system: "plus", help: "New Tab (⌘T)") {
                        model.newTab(url: StartPage.url.absoluteString)
                    }
                    BarButton(system: "square.on.square", help: "Show All Tabs (⇧⌘\\)") {
                        model.toggleOverview()
                    }
                }
            }

            addressBar
                .frame(maxWidth: 640)
                .padding(.leading, max(leading, trailing) + 10)
                .padding(.trailing, max(leading, trailing) + 10)
        }
        .animation(Motion.standard, value: model.downloads.isEmpty)
        // The address bar tracks the page: every URL change pushes through,
        // unless the user is mid-edit. A browser that shows a stale URL after
        // a redirect is worse than useless — you can't trust what you copy.
        // Every signal that the page moved. `urlText` covers ordinary
        // navigation and history moves; `isLoading` catches the case where a
        // redirect resolves to the same string we already had; and the tab
        // identity catches switching tabs. Syncing from all three is what
        // makes the bar track the page rather than lag a navigation behind.
        .onChange(of: tab.urlText) { _, _ in syncFromPage() }
        .onChange(of: tab.isLoading) { _, _ in syncFromPage() }
        .onChange(of: tab.id) { _, _ in
            addressEditing = false
            syncFromPage()
        }
        .onAppear { syncFromPage() }
    }

    private var addressBar: some View {
        let shape = RoundedRectangle(cornerRadius: 9, style: .continuous)
        return HStack(spacing: 6) {
            // Left: Reader when the page has an article to offer, otherwise
            // the page icon. Either one drags the page's address — onto the
            // Favourites bar, into Finder, into another app.
            Group {
                if isWebPage {
                    Button { model.toggleReader() } label: {
                        Image(systemName: model.reader == nil ? "doc.plaintext" : "doc.plaintext.fill")
                            .font(.system(size: 12))
                            .foregroundStyle(model.reader == nil ? Palette.textSecondary : Palette.accentText)
                            .frame(width: 18, height: 18)
                    }
                    .buttonStyle(.plain)
                    .help("Reader (⇧⌘R) — drag to save the page")
                } else {
                    Image(systemName: "magnifyingglass")
                        .font(.system(size: 11.5))
                        .foregroundStyle(Palette.textSecondary)
                        .frame(width: 18, height: 18)
                }
            }
            .onDrag {
                if let url = tab.webView.url { return NSItemProvider(object: url as NSURL) }
                return NSItemProvider()
            }

            if tab.isSecure && !addressFocused {
                Image(systemName: "lock.fill")
                    .font(.system(size: 9.5))
                    .foregroundStyle(Palette.textSecondary)
            }

            SelectAllField(
                text: $addressText,
                placeholder: "Search or enter website name",
                isFocused: Binding(
                    get: { addressFocused },
                    set: { focused in
                        addressFocused = focused
                        addressEditing = focused
                        // Leaving without submitting restores the loaded URL
                        // rather than stranding a half-typed string.
                        if !focused { addressText = tab.displayURL }
                    }),
                onCancel: onCancel,
                focusRequest: $model.addressFocusRequested,
                idleText: idleText,
                onSubmit: onSubmit)
            .frame(height: 18)

            // Zoom level, when not 100% — click to reset.
            if tab.zoom != 1 {
                Button { tab.resetZoom() } label: {
                    Text("\(Int(tab.zoom * 100))%")
                        .font(.app(10.5, .medium)).monospacedDigit()
                        .foregroundStyle(Palette.textSecondary)
                }
                .buttonStyle(.plain)
                .help("Actual Size (⌘0)")
            }

            Button {
                if tab.isLoading { tab.stop() } else { tab.reload() }
            } label: {
                Image(systemName: tab.isLoading ? "xmark" : "arrow.clockwise")
                    .font(.system(size: 11.5, weight: .medium))
                    .foregroundStyle(Palette.textSecondary)
                    .frame(width: 18, height: 18)
                    .contentShape(Rectangle())
            }
            .buttonStyle(PressSettleStyle())
            .help(tab.isLoading ? "Stop (⌘.)" : "Reload (⌘R)")
        }
        .padding(.horizontal, 8)
        .frame(height: 30)
        .background(addressFocused ? Palette.raised : Palette.text.opacity(0.06), in: shape)
        .overlay(shape.strokeBorder(Palette.separator.opacity(addressFocused ? 1 : 0.6), lineWidth: 1))
        // Focus ring outside the field, like an AppKit text field's.
        .overlay(
            RoundedRectangle(cornerRadius: 11, style: .continuous)
                .inset(by: -2)
                .stroke(Palette.accent.opacity(addressFocused ? 0.55 : 0), lineWidth: 3)
                .allowsHitTesting(false))
        .animation(Motion.hover, value: addressFocused)
    }
}

// MARK: - Tab chip

private struct TabTitleWidthKey: PreferenceKey {
    static let defaultValue: CGFloat = 0
    static func reduce(value: inout CGFloat, nextValue: () -> CGFloat) { value = max(value, nextValue()) }
}

private struct TabChip: View {
    @ObservedObject var tab: BrowserTab
    let isActive: Bool
    /// From `TabBarLayout`: every tab gets an exact share of the row.
    var width: CGFloat = 160
    /// Too narrow for a title — show the site icon alone.
    var compact = false
    /// The hairline between two neighbouring tabs, as Safari draws it.
    var showsDivider = false
    var isDragging = false
    let onSelect: () -> Void
    let onClose: () -> Void
    let onTogglePin: () -> Void
    /// Fires when a file is dragged over this chip long enough to mean it.
    let onDropFile: () -> Void
    /// For the right-click menu, shared with stacks and their dropdowns.
    var model: BrowserModel? = nil

    @State private var hovering = false
    @State private var closeHover = false
    @State private var dropTargeted = false
    @State private var springLoad: Task<Void, Never>?

    /// Host of whatever the tab currently shows, for the favicon lookup.
    private var host: String {
        guard let url = tab.webView.url, url.scheme?.hasPrefix("http") == true else { return "" }
        return url.host ?? ""
    }

    /// The title's natural width, measured once per title.
    @State private var titleWidth: CGFloat = 0

    /// Where everything inside a tab sits is computed as plain offsets from
    /// the tab's width, rather than left to stacks and truncation.
    ///
    /// The old layout centred an icon and a `…`-truncated title in an HStack.
    /// When tabs opened, closed or moved, every tab's frame animated to its
    /// new width — but the title re-truncated to the *final* width at once,
    /// and the icon jumped with it, so the contents lurched while the plate
    /// glided. Now the title is drawn at its natural width and fades out at
    /// the edge where it doesn't fit, as Safari's do, and its position is an
    /// offset — which animates in lockstep with the frame.
    var body: some View {
        let iconOnly = tab.isPinned || compact
        // Shown on the active tab and under the pointer, as Safari does — a
        // row of X's on every tab is noise. Hidden also means not clickable:
        // an invisible button that still takes clicks would eat a tab.
        let showClose = !tab.isPinned && (hovering || (isActive && !compact))
        let w = max(width, 1)
        let iconSize: CGFloat = tab.isPinned ? 15 : 14
        // Titled tabs keep the close button's width clear on both sides, so
        // the title stays centred whether or not the button is showing.
        let inset: CGFloat = iconOnly ? 0 : 26
        let room = max(w - inset * 2, 0)
        let content = iconOnly ? iconSize : iconSize + 6 + titleWidth
        let fits = content <= room
        let x: CGFloat = {
            if iconOnly {
                // An icon-only tab keeps its close button on the left like
                // every other tab; the icon steps aside for it under the
                // pointer rather than being replaced by it.
                return (compact && showClose) ? 25 : (w - iconSize) / 2
            }
            return fits ? inset + (room - content) / 2 : inset
        }()

        ZStack(alignment: .leading) {
            HStack(spacing: 6) {
                Group {
                    if tab.isLoading {
                        ProgressView()
                            .controlSize(.mini)
                            .scaleEffect(0.55)
                    } else {
                        Favicon(host: host, iconURL: tab.iconURL, size: iconSize,
                                tint: Palette.textSecondary)
                    }
                }
                .frame(width: iconSize, height: iconSize)
                if !iconOnly {
                    Text(tab.title)
                        .font(.app(12, isActive ? .medium : .regular))
                        .foregroundStyle(isActive ? Palette.text : Palette.textSecondary)
                        .lineLimit(1)
                        .fixedSize()
                        .background(GeometryReader { geo in
                            Color.clear.preference(key: TabTitleWidthKey.self, value: geo.size.width)
                        })
                }
            }
            .offset(x: x)
            .frame(width: w, height: 28, alignment: .leading)
            .mask {
                // Fade the title out where it runs past its room, instead of
                // an ellipsis that changes as the tab resizes.
                LinearGradient(
                    stops: fits || iconOnly
                        ? [.init(color: .black, location: 0), .init(color: .black, location: 1)]
                        : [.init(color: .black, location: 0),
                           .init(color: .black, location: max(0, (w - inset - 16) / w)),
                           .init(color: .clear, location: max(0, (w - inset) / w)),
                           .init(color: .clear, location: 1)],
                    startPoint: .leading, endPoint: .trailing)
            }
            .onPreferenceChange(TabTitleWidthKey.self) { titleWidth = $0 }

            if showClose {
                Button(action: onClose) {
                    Image(systemName: "xmark")
                        .font(.system(size: 8.5, weight: .bold))
                        .frame(width: 16, height: 16)
                        .background(closeHover ? Palette.text.opacity(0.1) : .clear, in: Circle())
                        .contentShape(Circle())
                }
                .buttonStyle(.plain)
                .foregroundStyle(Palette.textSecondary)
                .onHover { closeHover = $0 }
                .help("Close tab (⌘W) — ⌥-click to close the others")
                .offset(x: 7)
                .transition(.opacity)
            }
        }
        .frame(width: max(width, 1), height: 28)
        // Safari's treatment: the active tab is a raised plate, the rest sit
        // flush and gain a faint fill under the pointer.
        .background {
            let shape = RoundedRectangle(cornerRadius: 8, style: .continuous)
            if isActive {
                shape.fill(Palette.raised)
                    .overlay(shape.strokeBorder(Palette.separator, lineWidth: 1))
                    .shadow(color: .black.opacity(0.07), radius: 2, y: 1)
            } else if hovering {
                shape.fill(Palette.text.opacity(0.06))
            }
        }
        .overlay(alignment: .trailing) {
            Rectangle().fill(Palette.separator)
                .frame(width: 1, height: 16)
                .offset(x: 1.5)
                .opacity(showsDivider && !hovering ? 1 : 0)
        }
        .overlay {
            if dropTargeted {
                RoundedRectangle(cornerRadius: 8, style: .continuous)
                    .strokeBorder(Palette.accent, lineWidth: 1.5)
            }
        }
        // The chip being dragged fades so the insertion point is obvious.
        .opacity(isDragging ? 0.55 : 1)
        .contentShape(Rectangle())
        .onTapGesture(perform: onSelect)
        .animation(Motion.hover, value: hovering)
        .onHover { hovering = $0 }
        .help(tab.isPinned ? tab.title : "")
        .contextMenu {
            if let model { TabMenuItems(tab: tab, model: model) }
        }
        // Hovering a dragged file over a tab switches to it, the way holding a
        // file over a Finder folder springs it open — so you can drop onto a
        // page that isn't currently on screen.
        .onDrop(of: DropRouter.hoverTypes, isTargeted: $dropTargeted) { _ in
            false   // the tab itself never consumes the file; it only switches
        }
        .onChange(of: dropTargeted) { _, targeted in
            springLoad?.cancel()
            guard targeted, !isActive else { return }
            springLoad = Task {
                // Long enough that dragging across the strip on the way
                // somewhere else doesn't trip every tab it passes.
                try? await Task.sleep(for: .milliseconds(450))
                guard !Task.isCancelled else { return }
                await MainActor.run { onDropFile() }
            }
        }
        .onDisappear { springLoad?.cancel() }
    }
}

// MARK: - Security interstitial

/// Shown instead of a page the scanner refused. Says exactly what was wrong and
/// lets the user override, because a block with no explanation and no way past
/// just teaches people to turn the feature off.
private struct SecurityInterstitial: View {
    let verdict: ThreatVerdict
    @ObservedObject var tab: BrowserTab

    var body: some View {
        VStack(alignment: .leading, spacing: 11) {
            HStack(spacing: 9) {
                Image(systemName: verdict.level.icon)
                    .font(.app(19))
                    .foregroundStyle(Palette.danger)
                Text(verdict.level == .dangerous ? "Blocked this site" : "This site looks wrong")
                    .font(.app(16, .semibold))
                    .foregroundStyle(Palette.text)
            }

            ForEach(verdict.findings) { finding in
                VStack(alignment: .leading, spacing: 2) {
                    Text(finding.title)
                        .font(.app(12.5, .semibold))
                        .foregroundStyle(Palette.text)
                    Text(finding.detail)
                        .font(.app(11.5))
                        .foregroundStyle(Palette.textSecondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }

            HStack(spacing: 9) {
                PillButton("Go Back", style: .filled, size: .small) {
                    tab.securityBlock = nil
                    if tab.canGoBack { tab.goBack() } else { tab.goHome() }
                }
                PillButton("Continue Anyway", style: .ghost, size: .small) {
                    if let host = URL(string: verdict.subject)?.host {
                        ThreatScanner.shared.allowHost(host)
                    }
                    let target = verdict.subject
                    tab.securityBlock = nil
                    if let url = URL(string: target) {
                        tab.webView.load(URLRequest(url: url))
                    }
                }
            }
        }
        .padding(15)
        .frame(maxWidth: 560, alignment: .leading)
        .background(Palette.surface, in: RoundedRectangle(cornerRadius: 14))
        .overlay(RoundedRectangle(cornerRadius: 14)
            .strokeBorder(Palette.danger.opacity(0.55), lineWidth: 1))
        .shadow(color: .black.opacity(0.18), radius: 14, y: 5)
    }
}


// MARK: - Keychain key

/// The key drawn inside a focused sign-in field. Clicking it opens Apple's
/// Passwords picker (Touch ID) and fills the field — see `KeychainAutofill`.
private struct KeychainKey: View {
    @ObservedObject var tab: BrowserTab
    @AppStorage(AppSettings.Key.passwordAutofill) private var enabled = true
    @State private var hovering = false

    var body: some View {
        if enabled, let field = tab.credentialField, field.rect.height >= 18 {
            let zoom = tab.webView.pageZoom
            let size = min(max(field.rect.height * zoom - 10, 18), 24)
            Button {
                KeychainFillBridge.shared.begin(for: tab)
            } label: {
                Image(systemName: "key.fill")
                    .font(.system(size: size * 0.5, weight: .medium))
                    .foregroundStyle(Palette.onAccent)
                    .frame(width: size, height: size)
                    .background(Palette.accent.opacity(hovering ? 1 : 0.9),
                                in: RoundedRectangle(cornerRadius: 6, style: .continuous))
                    .shadow(color: .black.opacity(0.15), radius: 2, y: 1)
            }
            .buttonStyle(PressSettleStyle())
            .onHover { hovering = $0 }
            .help("Fill from Passwords (Touch ID)")
            // Inside the field, against its right edge, vertically centred.
            .offset(x: field.rect.maxX * zoom - size - 6,
                    y: field.rect.midY * zoom - size / 2)
            .transition(.opacity.combined(with: .scale(scale: 0.8)))
        }
    }
}

private extension View {
    /// A Dropdown-style strip slot: reports where it rests (for the dropdown's
    /// position and for drag geometry — measured *before* the drag offset),
    /// then follows the cursor while it's the one being dragged.
    func draggableSlot<G: Gesture>(key: String, dragging: String?, offset: CGFloat,
                                   gesture: G) -> some View {
        self
            .background(GeometryReader { geo in
                Color.clear.preference(key: StackFrameKey.self,
                                       value: [key: geo.frame(in: .named(BrowserSpace.name))])
            })
            // Faded like a dragged tab, so the insertion point is obvious.
            .opacity(dragging == key ? 0.55 : 1)
            .offset(x: dragging == key ? offset : 0)
            .zIndex(dragging == key ? 1 : 0)
            .gesture(gesture)
    }
}
