import SwiftUI
import WebKit
import UniformTypeIdentifiers

// The browser surfaces behind Safari's shortcuts: find bar, Favourites bar,
// bookmarks and Reading List sidebar, tab overview, Reader, downloads list,
// and the press-and-hold history menus.

// MARK: - Find bar (⌘F)

struct FindBar: View {
    @ObservedObject var model: BrowserModel
    @FocusState private var focused: Bool

    var body: some View {
        HStack(spacing: 8) {
            HStack(spacing: 6) {
                Image(systemName: "magnifyingglass")
                    .font(.system(size: 11.5))
                    .foregroundStyle(Palette.textSecondary)
                TextField("Find on page", text: $model.findQuery)
                    .textFieldStyle(.plain)
                    .font(.app(13))
                    .focused($focused)
                    .onSubmit {
                        // Return goes forward, ⇧Return back — as in Safari.
                        let back = NSApp.currentEvent?.modifierFlags.contains(.shift) ?? false
                        model.findNext(backwards: back)
                        focused = true
                    }
                    .onKeyPress(.escape) { model.closeFind(); return .handled }
                if !model.findQuery.isEmpty {
                    Text(model.findCount == 0 ? "Not found"
                         : "\(model.findIndex) of \(model.findCount)")
                        .font(.app(11.5)).monospacedDigit()
                        .foregroundStyle(model.findCount == 0 ? Palette.caution : Palette.textSecondary)
                        .transition(.opacity)
                }
            }
            .padding(.horizontal, 9)
            .frame(width: 300, height: 26)
            .background(Palette.raised, in: RoundedRectangle(cornerRadius: 7, style: .continuous))
            .overlay(RoundedRectangle(cornerRadius: 7, style: .continuous)
                .strokeBorder(Palette.separator, lineWidth: 1))

            BarButton(system: "chevron.up", help: "Previous (⇧⌘G)") { model.findNext(backwards: true) }
                .disabled(model.findCount == 0)
            BarButton(system: "chevron.down", help: "Next (⌘G)") { model.findNext() }
                .disabled(model.findCount == 0)
            Spacer()
            PillButton("Done", style: .ghost, size: .small) { model.closeFind() }
        }
        .padding(.horizontal, 10)
        .frame(height: 36)
        .background(Palette.tint)
        .overlay(alignment: .bottom) { Rectangle().fill(Palette.separator).frame(height: 1) }
        .onChange(of: model.findQuery) { _, _ in model.runFind() }
        .onChange(of: model.findFocusToken) { _, _ in focused = true }
        .onAppear {
            DispatchQueue.main.async { focused = true }
            // Reopened with an earlier search: count it on this page.
            model.runFind()
        }
        .animation(Motion.quick, value: model.findCount)
    }
}

// MARK: - History buttons (press and hold)

/// Back or Forward. A click goes one step; pressing and holding lists the
/// pages in that direction — by title, or by address with ⌥ held — exactly
/// as Safari's do. `Menu` with a primary action gives that behaviour natively.
struct HistoryButton: View {
    @ObservedObject var tab: BrowserTab
    let forward: Bool
    @State private var optionHeld = false
    @State private var monitor: Any?

    private var items: [WKBackForwardListItem] {
        let list = tab.webView.backForwardList
        return forward ? list.forwardList : list.backList.reversed()
    }

    var body: some View {
        Menu {
            ForEach(Array(items.prefix(20).enumerated()), id: \.offset) { _, item in
                Button(optionHeld ? item.url.absoluteString
                                  : (item.title?.isEmpty == false ? item.title! : item.url.host ?? "Page")) {
                    tab.webView.go(to: item)
                }
            }
        } label: {
            Image(systemName: forward ? "chevron.right" : "chevron.left")
                .font(.system(size: 13, weight: .regular))
        } primaryAction: {
            if forward { tab.goForward() } else { tab.goBack() }
        }
        .menuStyle(.borderlessButton)
        .menuIndicator(.hidden)
        .fixedSize()
        .frame(width: 26, height: 24)
        .foregroundStyle(Palette.text.opacity(0.75))
        .disabled(forward ? !tab.canGoForward : !tab.canGoBack)
        .help(forward ? "Forward (⌘]) — hold for history" : "Back (⌘[) — hold for history")
        .onAppear {
            monitor = NSEvent.addLocalMonitorForEvents(matching: .flagsChanged) { event in
                optionHeld = event.modifierFlags.contains(.option)
                return event
            }
        }
        .onDisappear { if let monitor { NSEvent.removeMonitor(monitor) } }
    }
}

// MARK: - Favourites bar

struct FavouritesBar: View {
    @ObservedObject var model: BrowserModel
    @ObservedObject private var store = LibraryStore.shared
    @State private var frames: [UUID: CGRect] = [:]
    @State private var dragging: UUID?
    @State private var drag: CGSize = .zero
    @State private var dropTargeted = false

    private static let space = "krackn.favbar"
    /// Pulled further up than this, a dragged favourite is removed on release.
    private static let tearAway: CGFloat = -22

    var body: some View {
        HStack(spacing: 2) {
            ForEach(store.library.favourites) { node in
                FavouriteChip(node: node, model: model, removing: dragging == node.id && drag.height < Self.tearAway)
                    .background(GeometryReader { geo in
                        Color.clear.preference(key: TabReorder.FramePreference.self,
                                               value: [node.id: geo.frame(in: .named(Self.space))])
                    })
                    .offset(dragging == node.id ? drag : .zero)
                    .zIndex(dragging == node.id ? 1 : 0)
                    .gesture(dragGesture(node))
            }
            Spacer(minLength: 0)
        }
        .padding(.horizontal, 8)
        .frame(height: 26)
        .background(dropTargeted ? Palette.accent.opacity(0.12) : .clear)
        .coordinateSpace(name: Self.space)
        .onPreferenceChange(TabReorder.FramePreference.self) { frames = $0 }
        // Drop a link — the address bar's icon, or one from a page — to add it.
        // `.url` only: never `.item`, which would swallow right-clicks.
        .onDrop(of: [.url], isTargeted: $dropTargeted) { providers in
            guard let provider = providers.first else { return false }
            _ = provider.loadObject(ofClass: URL.self) { url, _ in
                guard let url, url.scheme?.hasPrefix("http") == true else { return }
                Task { @MainActor in
                    let title = model.active?.webView.url == url ? (model.active?.title ?? "")
                                                                 : (url.host ?? url.absoluteString)
                    withAnimation(Motion.standard) {
                        store.addFavourite(title: title.isEmpty ? (url.host ?? "") : title,
                                           url: url.absoluteString)
                    }
                }
            }
            return true
        }
        .animation(Motion.standard, value: store.library.favourites)
    }

    /// Drag sideways to reorder, or up and off the bar to remove — Safari's
    /// gestures for the Favourites bar.
    private func dragGesture(_ node: BookmarkNode) -> some Gesture {
        DragGesture(minimumDistance: 4, coordinateSpace: .named(Self.space))
            .onChanged { value in
                dragging = node.id
                drag = value.translation
            }
            .onEnded { value in
                let favourites = store.library.favourites
                defer {
                    withAnimation(Motion.standard) { dragging = nil; drag = .zero }
                }
                if value.translation.height < Self.tearAway {
                    withAnimation(Motion.standard) { store.remove([node.id]) }
                    return
                }
                let ordered = favourites.compactMap { frames[$0.id] }
                guard ordered.count == favourites.count,
                      let from = favourites.firstIndex(where: { $0.id == node.id }),
                      let frame = frames[node.id] else { return }
                let target = TabReorder.targetIndex(dragging: from,
                                                    visualCenterX: frame.midX + value.translation.width,
                                                    frames: ordered)
                guard target != from else { return }
                withAnimation(Motion.standard) {
                    store.moveFavourite(from: from, to: target > from ? target + 1 : target)
                }
            }
    }
}

private struct FavouriteChip: View {
    let node: BookmarkNode
    @ObservedObject var model: BrowserModel
    var removing = false
    @State private var hovering = false
    @State private var folderOpen = false

    private var host: String { node.url.flatMap { URL(string: $0)?.host } ?? "" }

    var body: some View {
        HStack(spacing: 5) {
            if node.isFolder {
                Image(systemName: "folder").font(.system(size: 11))
                    .foregroundStyle(Palette.textSecondary)
            } else {
                Favicon(host: host, size: 12)
            }
            Text(node.title)
                .font(.app(12))
                .foregroundStyle(Palette.text)
                .lineLimit(1)
                .frame(maxWidth: 140, alignment: .leading)
            if node.isFolder {
                Image(systemName: "chevron.down").font(.system(size: 7.5, weight: .semibold))
                    .foregroundStyle(Palette.textSecondary)
            }
        }
        .padding(.horizontal, 7)
        .frame(height: 22)
        .fixedSize(horizontal: true, vertical: false)
        .background(RoundedRectangle(cornerRadius: 5, style: .continuous)
            .fill(hovering || folderOpen ? Palette.text.opacity(0.07) : .clear))
        .opacity(removing ? 0.35 : 1)
        .scaleEffect(removing ? 0.9 : 1)
        .contentShape(Rectangle())
        .onHover { hovering = $0 }
        .animation(Motion.hover, value: hovering)
        .animation(Motion.quick, value: removing)
        .onTapGesture { open() }

        .help(node.url ?? node.title)
        .popover(isPresented: $folderOpen, arrowEdge: .bottom) {
            VStack(alignment: .leading, spacing: 1) {
                ForEach(node.children ?? []) { child in
                    FolderMenuRow(node: child) {
                        folderOpen = false
                        if let url = child.url { model.active?.go(to: url) }
                    }
                }
                if (node.children ?? []).isEmpty {
                    Text("Empty").font(.app(12)).foregroundStyle(Palette.textSecondary).padding(8)
                }
            }
            .padding(6)
            .frame(minWidth: 220)
        }
        .contextMenu {
            if let url = node.url {
                Button("Open in New Tab") { model.newTab(url: url, activate: false) }
                Button("Copy Address") {
                    NSPasteboard.general.clearContents()
                    NSPasteboard.general.setString(url, forType: .string)
                }
            } else {
                Button("Open All in Tabs") { openAll() }
            }
            Divider()
            Button("Remove") { withAnimation(Motion.standard) { LibraryStore.shared.remove([node.id]) } }
        }
    }

    private func open() {
        let command = NSEvent.modifierFlags.contains(.command)
        if node.isFolder {
            // ⌘-click a folder opens everything in it.
            if command { openAll() } else { folderOpen.toggle() }
            return
        }
        guard let url = node.url else { return }
        if command {
            model.newTab(url: url, activate: NSEvent.modifierFlags.contains(.shift))
        } else {
            model.active?.go(to: url)
        }
    }

    private func openAll() {
        for url in LibraryOps.urls(in: node) { model.newTab(url: url, activate: false) }
    }
}

private struct FolderMenuRow: View {
    let node: BookmarkNode
    let action: () -> Void
    @State private var hovering = false
    var body: some View {
        HStack(spacing: 7) {
            if node.isFolder {
                Image(systemName: "folder").font(.system(size: 11))
            } else {
                Favicon(host: node.url.flatMap { URL(string: $0)?.host } ?? "", size: 12)
            }
            Text(node.title).font(.app(12.5)).lineLimit(1)
            Spacer()
        }
        .padding(.horizontal, 8).frame(height: 26)
        .background(RoundedRectangle(cornerRadius: 5).fill(hovering ? Palette.selection.opacity(0.8) : .clear))
        .contentShape(Rectangle())
        .onHover { hovering = $0 }
        .onTapGesture(perform: action)
    }
}

// MARK: - Bookmarks and Reading List sidebar (⌃⌘1, ⌃⌘2)

struct LibrarySidebar: View {
    @ObservedObject var model: BrowserModel

    var body: some View {
        VStack(spacing: 0) {
            HStack(spacing: 6) {
                SegmentedControl(
                    options: [(BrowserModel.LibraryPane.bookmarks, "Bookmarks"),
                              (BrowserModel.LibraryPane.readingList, "Reading List")],
                    selection: Binding(get: { model.libraryPane ?? .bookmarks },
                                       set: { model.libraryPane = $0 }))
                BarButton(system: "xmark", help: "Close") {
                    withAnimation(Motion.surface) { model.libraryPane = nil }
                }
            }
            .padding(10)

            Group {
                if model.libraryPane == .readingList {
                    ReadingListPane(model: model)
                } else {
                    BookmarksPane(model: model)
                }
            }
            .frame(maxHeight: .infinity)
        }
        .frame(width: 260)
        .background(Palette.tint.opacity(0.7))
        .overlay(alignment: .trailing) { Rectangle().fill(Palette.separator).frame(width: 1) }
    }
}

private struct BookmarksPane: View {
    @ObservedObject var model: BrowserModel
    @ObservedObject private var store = LibraryStore.shared
    @State private var expanded: Set<UUID> = [BrowserLibrary.favouritesID]
    @State private var selection: Set<UUID> = []
    @State private var anchor: UUID?
    @State private var renaming: UUID?
    @State private var renameText = ""
    @FocusState private var listFocused: Bool
    @FocusState private var renameFocused: Bool

    private var rows: [(node: BookmarkNode, depth: Int)] {
        LibraryOps.visibleRows(store.library.bookmarks, expanded: expanded)
    }

    var body: some View {
        VStack(spacing: 0) {
            ScrollView {
                VStack(spacing: 1) {
                    ForEach(rows, id: \.node.id) { row in
                        rowView(row.node, depth: row.depth)
                    }
                }
                .padding(.horizontal, 8)
            }
            .focusable()
            .focused($listFocused)
            .focusEffectDisabled()
            .onKeyPress(.upArrow) { step(-1); return .handled }
            .onKeyPress(.downArrow) { step(1); return .handled }
            .onKeyPress(.space) { openSelected(); return .handled }
            .onKeyPress(.rightArrow) { setExpanded(true); return .handled }
            .onKeyPress(.leftArrow) { setExpanded(false); return .handled }
            .onKeyPress(.return) { startRename(); return .handled }
            .onKeyPress(.delete) { deleteSelected(); return .handled }
            .onKeyPress(.deleteForward) { deleteSelected(); return .handled }

            HStack {
                Button {
                    // ⌥-click: put the selected items into the new folder.
                    let group = NSEvent.modifierFlags.contains(.option)
                    withAnimation(Motion.standard) {
                        if let id = store.newFolder(grouping: group ? selection : []) {
                            selection = [id]
                            expanded.insert(id)
                            startRename(id)
                        }
                    }
                } label: {
                    Label("New Folder", systemImage: "folder.badge.plus").font(.app(12))
                }
                .buttonStyle(.plain)
                .foregroundStyle(Palette.textSecondary)
                .help("New Folder — ⌥-click to put the selection inside it")
                Spacer()
            }
            .padding(10)
        }
        .animation(Motion.quick, value: expanded)
    }

    private func rowView(_ node: BookmarkNode, depth: Int) -> some View {
        let selected = selection.contains(node.id)
        return HStack(spacing: 6) {
            if node.isFolder {
                Image(systemName: "chevron.right")
                    .font(.system(size: 8.5, weight: .semibold))
                    .rotationEffect(.degrees(expanded.contains(node.id) ? 90 : 0))
                    .frame(width: 10)
                    .foregroundStyle(Palette.textSecondary)
                    .onTapGesture { toggle(node.id) }
                Image(systemName: node.id == BrowserLibrary.favouritesID ? "star" : "folder")
                    .font(.system(size: 11.5))
                    .foregroundStyle(Palette.accentText)
            } else {
                Color.clear.frame(width: 10)
                Favicon(host: node.url.flatMap { URL(string: $0)?.host } ?? "", size: 13)
            }
            if renaming == node.id {
                TextField("", text: $renameText)
                    .textFieldStyle(.plain)
                    .font(.app(12.5))
                    .focused($renameFocused)
                    .onSubmit { commitRename() }
                    .onKeyPress(.escape) { renaming = nil; listFocused = true; return .handled }
            } else {
                Text(node.title).font(.app(12.5)).lineLimit(1)
                    .foregroundStyle(Palette.text)
            }
            Spacer(minLength: 0)
        }
        .padding(.leading, CGFloat(depth) * 14 + 4)
        .padding(.trailing, 6)
        .frame(height: 26)
        .background(RoundedRectangle(cornerRadius: 6, style: .continuous)
            .fill(selected ? Palette.selection.opacity(0.9) : .clear))
        .contentShape(Rectangle())
        .onTapGesture { click(node) }
        .simultaneousGesture(TapGesture(count: 2).onEnded { open(node) })
        .contextMenu {
            if let url = node.url {
                Button("Open") { model.active?.go(to: url) }
                Button("Open in New Tab") { model.newTab(url: url, activate: false) }
            } else {
                Button("Open All in Tabs") {
                    LibraryOps.urls(in: node).forEach { model.newTab(url: $0, activate: false) }
                }
            }
            Button("Rename") { startRename(node.id) }
            if node.id != BrowserLibrary.favouritesID {
                Divider()
                Button("Delete") { withAnimation(Motion.standard) { store.remove([node.id]) } }
            }
        }
    }

    // MARK: Selection — click, ⌘-click to add, ⇧-click to extend

    private func click(_ node: BookmarkNode) {
        listFocused = true
        let flags = NSEvent.modifierFlags
        if flags.contains(.command) {
            if selection.contains(node.id) { selection.remove(node.id) } else { selection.insert(node.id) }
            anchor = node.id
        } else if flags.contains(.shift), let anchor,
                  let a = rows.firstIndex(where: { $0.node.id == anchor }),
                  let b = rows.firstIndex(where: { $0.node.id == node.id }) {
            selection = Set(rows[min(a, b)...max(a, b)].map(\.node.id))
        } else {
            selection = [node.id]
            anchor = node.id
        }
    }

    private func step(_ delta: Int) {
        let ids = rows.map(\.node.id)
        guard !ids.isEmpty else { return }
        let current = anchor.flatMap { ids.firstIndex(of: $0) } ?? (delta > 0 ? -1 : ids.count)
        let next = min(max(current + delta, 0), ids.count - 1)
        selection = [ids[next]]
        anchor = ids[next]
    }

    private var selectedNode: BookmarkNode? {
        anchor.flatMap { LibraryOps.find($0, in: store.library.bookmarks) }
    }

    private func openSelected() { if let node = selectedNode { open(node) } }

    private func open(_ node: BookmarkNode) {
        if node.isFolder { toggle(node.id) } else if let url = node.url { model.active?.go(to: url) }
    }

    private func toggle(_ id: UUID) {
        if expanded.contains(id) { expanded.remove(id) } else { expanded.insert(id) }
    }

    private func setExpanded(_ open: Bool) {
        guard let node = selectedNode, node.isFolder else { return }
        if open { expanded.insert(node.id) } else { expanded.remove(node.id) }
    }

    private func startRename(_ id: UUID? = nil) {
        guard let id = id ?? anchor, let node = LibraryOps.find(id, in: store.library.bookmarks) else { return }
        renameText = node.title
        renaming = id
        DispatchQueue.main.async { renameFocused = true }
    }

    private func commitRename() {
        if let id = renaming { store.rename(id, to: renameText) }
        renaming = nil
        listFocused = true
    }

    private func deleteSelected() {
        guard renaming == nil, !selection.isEmpty else { return }
        withAnimation(Motion.standard) { store.remove(selection) }
        selection = []
    }
}

private struct ReadingListPane: View {
    @ObservedObject var model: BrowserModel
    @ObservedObject private var store = LibraryStore.shared
    @State private var selected: UUID?
    @FocusState private var focused: Bool

    var body: some View {
        ScrollView {
            VStack(spacing: 2) {
                if store.library.readingList.isEmpty {
                    Text("Press ⇧⌘D to save this page, or ⇧-click a link.")
                        .font(.app(12))
                        .foregroundStyle(Palette.textSecondary)
                        .multilineTextAlignment(.center)
                        .padding(.top, 30)
                        .padding(.horizontal, 16)
                }
                ForEach(store.library.readingList) { item in
                    HStack(alignment: .top, spacing: 8) {
                        Circle().fill(item.isRead ? .clear : Palette.accent)
                            .frame(width: 6, height: 6).padding(.top, 6)
                        VStack(alignment: .leading, spacing: 2) {
                            Text(item.title).font(.app(12.5, .medium)).lineLimit(2)
                                .foregroundStyle(Palette.text)
                            Text(URL(string: item.url)?.host ?? item.url)
                                .font(.app(11)).foregroundStyle(Palette.textSecondary)
                        }
                        Spacer(minLength: 0)
                    }
                    .padding(8)
                    .background(RoundedRectangle(cornerRadius: 7, style: .continuous)
                        .fill(selected == item.id ? Palette.selection.opacity(0.9) : .clear))
                    .contentShape(Rectangle())
                    .onTapGesture {
                        selected = item.id
                        focused = true
                        model.active?.go(to: item.url)
                        store.setRead(item.id, true)
                    }
                    .contextMenu {
                        Button(item.isRead ? "Mark as Unread" : "Mark as Read") {
                            store.setRead(item.id, !item.isRead)
                        }
                        Button("Open in New Tab") { model.newTab(url: item.url, activate: false) }
                        Divider()
                        Button("Remove") { withAnimation(Motion.standard) { store.removeReading(item.id) } }
                    }
                    .transition(.asymmetric(insertion: .opacity,
                                            removal: .move(edge: .leading).combined(with: .opacity)))
                }
            }
            .padding(.horizontal, 8)
        }
        .focusable()
        .focused($focused)
        .focusEffectDisabled()
        .onKeyPress(.delete) { removeSelected(); return .handled }
        .onKeyPress(.deleteForward) { removeSelected(); return .handled }
        .animation(Motion.standard, value: store.library.readingList)
    }

    private func removeSelected() {
        guard let selected else { return }
        withAnimation(Motion.standard) { store.removeReading(selected) }
        self.selected = nil
    }
}

// MARK: - Tab overview (⇧⌘\)

struct TabOverview: View {
    @ObservedObject var model: BrowserModel

    var body: some View {
        ZStack {
            Rectangle().fill(.ultraThinMaterial)
                .overlay(Palette.background.opacity(0.75))
                .onTapGesture { close() }
            ScrollView {
                LazyVGrid(columns: [GridItem(.adaptive(minimum: 230, maximum: 320), spacing: 18)],
                          spacing: 18) {
                    ForEach(model.tabs) { tab in
                        OverviewCard(tab: tab, isActive: tab.id == model.activeID) {
                            model.activate(tab)
                            close()
                        } onClose: {
                            withAnimation(Motion.standard) { model.close(tab) }
                        }
                    }
                    NewTabCard {
                        model.newTab(url: StartPage.url.absoluteString)
                        close()
                    }
                }
                .padding(28)
            }
        }
        .background(
            Button("") { close() }.keyboardShortcut(.cancelAction).opacity(0)
        )
    }

    private func close() {
        withAnimation(Motion.surface) { model.showsOverview = false }
    }
}

private struct OverviewCard: View {
    @ObservedObject var tab: BrowserTab
    let isActive: Bool
    let onSelect: () -> Void
    let onClose: () -> Void
    @State private var hovering = false

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            ZStack {
                RoundedRectangle(cornerRadius: 10, style: .continuous).fill(Palette.surface)
                if let image = tab.thumbnail {
                    Image(nsImage: image)
                        .resizable()
                        .aspectRatio(contentMode: .fill)
                } else {
                    Favicon(host: tab.webView.url?.host ?? "", iconURL: tab.iconURL, size: 30)
                }
            }
            .aspectRatio(16 / 10, contentMode: .fit)
            .clipShape(RoundedRectangle(cornerRadius: 10, style: .continuous))
            .overlay(RoundedRectangle(cornerRadius: 10, style: .continuous)
                .strokeBorder(isActive ? Palette.accent : Palette.separator,
                              lineWidth: isActive ? 2 : 1))
            .overlay(alignment: .topLeading) {
                if hovering {
                    Button(action: onClose) {
                        Image(systemName: "xmark")
                            .font(.system(size: 9, weight: .bold))
                            .frame(width: 20, height: 20)
                            .background(.regularMaterial, in: Circle())
                    }
                    .buttonStyle(.plain)
                    .padding(7)
                    .transition(.opacity)
                }
            }
            .shadow(color: .black.opacity(hovering ? 0.16 : 0.08), radius: hovering ? 14 : 6, y: 4)

            HStack(spacing: 6) {
                Favicon(host: tab.webView.url?.host ?? "", iconURL: tab.iconURL, size: 12)
                Text(tab.title).font(.app(12)).lineLimit(1).foregroundStyle(Palette.text)
            }
        }
        .scaleEffect(hovering ? 1.02 : 1)
        .contentShape(Rectangle())
        .onHover { hovering = $0 }
        .onTapGesture(perform: onSelect)
        .animation(Motion.quick, value: hovering)
    }
}

private struct NewTabCard: View {
    let action: () -> Void
    @State private var hovering = false
    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            RoundedRectangle(cornerRadius: 10, style: .continuous)
                .strokeBorder(Palette.separator, style: StrokeStyle(lineWidth: 1, dash: [5, 4]))
                .background(RoundedRectangle(cornerRadius: 10).fill(hovering ? Palette.controlFill : .clear))
                .overlay(Image(systemName: "plus").font(.system(size: 22, weight: .light))
                    .foregroundStyle(Palette.textSecondary))
                .aspectRatio(16 / 10, contentMode: .fit)
            Text("New Tab").font(.app(12)).foregroundStyle(Palette.textSecondary)
        }
        .contentShape(Rectangle())
        .onHover { hovering = $0 }
        .onTapGesture(perform: action)
    }
}

// MARK: - Reader (⇧⌘R)

struct ReaderOverlay: View {
    let article: ReaderArticle
    let onClose: () -> Void

    var body: some View {
        ZStack(alignment: .topTrailing) {
            ReaderWebView(article: article)
            BarButton(system: "xmark", help: "Close Reader (Esc)", action: onClose)
                .keyboardShortcut(.cancelAction)
                .padding(12)
        }
        .background(Palette.background)
    }
}

/// Renders the extracted article in its own web view: no scripts, nothing
/// stored, styled from the app's palette with Apple's reading face.
private struct ReaderWebView: NSViewRepresentable {
    let article: ReaderArticle

    func makeNSView(context: Context) -> WKWebView {
        let config = WKWebViewConfiguration()
        config.websiteDataStore = .nonPersistent()
        config.defaultWebpagePreferences.allowsContentJavaScript = false
        let view = WKWebView(frame: .zero, configuration: config)
        // Transparent, so the palette background shows. Undeclared API, so
        // guarded: an unknown KVC key raises an exception Swift can't catch.
        if view.responds(to: NSSelectorFromString("_setDrawsBackground:")) {
            view.setValue(false, forKey: "drawsBackground")
        }
        view.loadHTMLString(html, baseURL: article.baseURL)
        return view
    }

    func updateNSView(_ nsView: WKWebView, context: Context) {}

    private var html: String {
        let light = Palette.lightTokens, dark = Palette.darkTokens
        func esc(_ s: String) -> String {
            s.replacingOccurrences(of: "&", with: "&amp;").replacingOccurrences(of: "<", with: "&lt;")
        }
        return """
        <!doctype html><html><head><meta charset="utf-8">
        <style>
        :root { --bg: \(light.background); --text: \(light.text); --muted: \(light.textSecondary); --link: \(light.accentInk); }
        @media (prefers-color-scheme: dark) { :root { --bg: \(dark.background); --text: \(dark.text); --muted: \(dark.textSecondary); --link: \(dark.accentInk); } }
        html { background: var(--bg); }
        body { max-width: 660px; margin: 56px auto 96px; padding: 0 28px; color: var(--text);
               font: 19px/1.62 ui-serif, "New York", Georgia, serif; -webkit-font-smoothing: antialiased; }
        .site { font: 600 12px/1 -apple-system, sans-serif; letter-spacing: .04em; text-transform: uppercase; color: var(--muted); }
        h1 { font: 700 32px/1.2 -apple-system, sans-serif; letter-spacing: -0.02em; margin: 10px 0 6px; }
        .by { font: 14px -apple-system, sans-serif; color: var(--muted); margin-bottom: 34px; }
        h2, h3, h4 { font-family: -apple-system, sans-serif; line-height: 1.3; margin-top: 1.6em; }
        a { color: var(--link); text-decoration: none; }
        img, video, figure { max-width: 100%; height: auto; border-radius: 6px; }
        figcaption { font: 13px -apple-system, sans-serif; color: var(--muted); }
        blockquote { margin: 1.2em 0; padding-left: 18px; border-left: 3px solid var(--muted); color: var(--muted); }
        pre, code { font: 14px ui-monospace, Menlo, monospace; }
        pre { overflow-x: auto; padding: 14px; border-radius: 8px; background: rgba(127,127,127,.12); }
        </style></head><body>
        <div class="site">\(esc(article.site))</div>
        <h1>\(esc(article.title))</h1>
        \(article.byline.isEmpty ? "" : "<div class=\"by\">\(esc(article.byline))</div>")
        \(article.html)
        </body></html>
        """
    }
}

// MARK: - Downloads list

struct DownloadsButton: View {
    @ObservedObject var model: BrowserModel
    @State private var open = false

    private var running: Bool { model.downloads.contains { $0.state == .running } }

    var body: some View {
        BarButton(system: running ? "arrow.down.circle.dotted" : "arrow.down.circle",
                  help: "Downloads") { open.toggle() }
            .symbolEffect(.pulse, isActive: running)
            .popover(isPresented: $open, arrowEdge: .bottom) {
                VStack(alignment: .leading, spacing: 2) {
                    HStack {
                        Text("Downloads").font(.app(13, .semibold))
                        Spacer()
                        Button("Clear") {
                            withAnimation(Motion.standard) {
                                model.downloads.removeAll { $0.state != .running }
                            }
                        }
                        .buttonStyle(.plain)
                        .font(.app(12))
                        .foregroundStyle(Palette.textSecondary)
                    }
                    .padding(.horizontal, 8)
                    .padding(.bottom, 4)
                    ForEach(model.downloads) { item in DownloadRow(item: item) }
                }
                .padding(10)
                .frame(width: 340)
            }
    }
}

private struct DownloadRow: View {
    let item: DownloadItem
    @State private var hovering = false

    var body: some View {
        HStack(spacing: 9) {
            FileIcon(url: item.file, size: 28)
            VStack(alignment: .leading, spacing: 2) {
                Text(item.file.lastPathComponent).font(.app(12.5)).lineLimit(1)
                    .truncationMode(.middle)
                Group {
                    switch item.state {
                    case .running: Text("Downloading…")
                    case .done(let notice): Text(notice).lineLimit(1)
                    case .failed(let message): Text(message).foregroundStyle(Palette.caution).lineLimit(1)
                    }
                }
                .font(.app(11))
                .foregroundStyle(Palette.textSecondary)
            }
            Spacer(minLength: 4)
            if item.state == .running {
                ProgressView().controlSize(.small)
            } else {
                BarButton(system: "magnifyingglass", help: "Show in Finder") {
                    NSWorkspace.shared.activateFileViewerSelecting([item.file])
                }
            }
        }
        .padding(6)
        .background(RoundedRectangle(cornerRadius: 7).fill(hovering ? Palette.controlFill : .clear))
        .contentShape(Rectangle())
        .onHover { hovering = $0 }
        // Double-click opens the file, as in Safari's list.
        .onTapGesture(count: 2) { NSWorkspace.shared.open(item.file) }
        .onDrag { NSItemProvider.file(item.file) }
    }
}

// MARK: - Tab stacks (Dropdown tab style)

/// The right-click menu for a tab — shared by tab chips, stack chips and the
/// rows of a stack's dropdown, so a tab offers the same actions wherever it is.
struct TabMenuItems: View {
    @ObservedObject var tab: BrowserTab
    let model: BrowserModel

    var body: some View {
        Button(tab.isPinned ? "Unpin Tab" : "Pin Tab") { model.togglePin(tab) }
        Divider()
        Button("Reload") { tab.reload() }
        Button("Open in Default Browser") {
            if let url = tab.webView.url { NSWorkspace.shared.open(url) }
        }
        Button("Copy Address") {
            NSPasteboard.general.clearContents()
            NSPasteboard.general.setString(tab.displayURL, forType: .string)
        }
        Divider()
        Button("Copy Page Diagnostics") {
            Task {
                let report = await tab.pageDiagnostics()
                NSPasteboard.general.clearContents()
                NSPasteboard.general.setString(report, forType: .string)
            }
        }
        Divider()
        Button("Close Tab") { withAnimation(Motion.standard) { model.close(tab) } }
            .disabled(tab.isPinned)
    }
}

/// Where each stack chip sits, so its dropdown can open right beneath it.
struct StackFrameKey: PreferenceKey {
    static let defaultValue: [String: CGRect] = [:]
    static func reduce(value: inout [String: CGRect], nextValue: () -> [String: CGRect]) {
        value.merge(nextValue()) { _, new in new }
    }
}

enum BrowserSpace { static let name = "krackn.browser" }

/// One tab-strip slot standing for every open page on a site. Shows the
/// site's icon and how many pages it holds; on the stack you're in, the title
/// of the page you're on. Clicking it opens `TabStackDropdown`.
struct TabStackChip: View {
    let tabs: [BrowserTab]
    let domain: String
    let key: String
    @ObservedObject var model: BrowserModel
    let isActive: Bool
    var width: CGFloat
    var compact: Bool
    var showsDivider: Bool
    let isOpen: Bool
    let onToggle: () -> Void
    /// A dragged file held over the stack: open the dropdown, and say whether
    /// the drag is still over the stack (so the strip can close it again).
    let onDragOpen: () -> Void
    let onDragPresence: (Bool) -> Void
    @State private var hovering = false
    @State private var dropTargeted = false
    @State private var springLoad: Task<Void, Never>?

    /// The page the stack shows: the one you're on, if it's in here.
    private var lead: BrowserTab { tabs.first { $0.id == model.activeID } ?? tabs[0] }

    var body: some View {
        let shape = RoundedRectangle(cornerRadius: 8, style: .continuous)
        StackLabel(tab: lead, domain: domain, showsPageTitle: isActive, count: tabs.count,
                   width: max(width, 1), compact: compact, isActive: isActive, isOpen: isOpen)
            .frame(width: max(width, 1), height: 28)
            .background {
                if isActive {
                    shape.fill(Palette.raised)
                        .overlay(shape.strokeBorder(Palette.separator, lineWidth: 1))
                        .shadow(color: .black.opacity(0.07), radius: 2, y: 1)
                } else if hovering || isOpen {
                    shape.fill(Palette.text.opacity(0.06))
                }
            }
            .overlay(alignment: .trailing) {
                Rectangle().fill(Palette.separator)
                    .frame(width: 1, height: 16)
                    .offset(x: 1.5)
                    .opacity(showsDivider && !hovering && !isOpen ? 1 : 0)
            }
            .contentShape(Rectangle())
            .onTapGesture(perform: onToggle)
            .onHover { hovering = $0 }
            .animation(Motion.hover, value: hovering)
            .help("\(tabs.count) tabs from \(domain)")
            // Right-click: the page the stack is showing, plus the stack itself.
            .contextMenu {
                TabMenuItems(tab: lead, model: model)
                Divider()
                Button("Close All \(tabs.count) Tabs from \(domain)") {
                    withAnimation(Motion.standard) { tabs.forEach { model.close($0) } }
                }
            }
            // Like a tab, but a stack springs open its list instead of
            // switching, so the file can be carried on to the right page.
            .onDrop(of: DropRouter.hoverTypes, isTargeted: $dropTargeted) { _ in false }
            .onChange(of: dropTargeted) { _, targeted in
                onDragPresence(targeted)
                springLoad?.cancel()
                guard targeted, !isOpen else { return }
                springLoad = Task {
                    try? await Task.sleep(for: .milliseconds(450))
                    guard !Task.isCancelled else { return }
                    await MainActor.run { onDragOpen() }
                }
            }
            .onDisappear { springLoad?.cancel() }
    }
}

private struct StackLabel: View {
    @ObservedObject var tab: BrowserTab
    let domain: String
    let showsPageTitle: Bool
    let count: Int
    let width: CGFloat
    let compact: Bool
    let isActive: Bool
    let isOpen: Bool
    @State private var labelWidth: CGFloat = 0

    var body: some View {
        let chevron = Image(systemName: "chevron.down")
            .font(.system(size: 7.5, weight: .bold))
            .foregroundStyle(Palette.textSecondary)
            .rotationEffect(.degrees(isOpen ? 180 : 0))
            .animation(Motion.quick, value: isOpen)
        let badge = Text("\(count)")
            .font(.app(10, .semibold)).monospacedDigit()
            .foregroundStyle(Palette.textSecondary)
            .padding(.horizontal, 5)
            .frame(height: 15)
            .background(Palette.text.opacity(0.09), in: Capsule())
        if compact {
            // Icon-only: the count rides on the icon's corner.
            Favicon(host: tab.webView.url?.host ?? "", iconURL: tab.iconURL, size: 14,
                    tint: Palette.textSecondary)
                .overlay(alignment: .topTrailing) {
                    Text("\(count)")
                        .font(.app(8.5, .bold)).monospacedDigit()
                        .foregroundStyle(Palette.onAccent)
                        .padding(.horizontal, 3)
                        .frame(minWidth: 12, minHeight: 12)
                        .background(Palette.accent, in: Capsule())
                        .offset(x: 8, y: -6)
                }
        } else {
            // Same rule as a tab: centred when it fits; otherwise the title
            // fades out at its edge while the count and chevron stay put —
            // no re-truncation while the chip resizes.
            let room = max(width - 20, 0)
            let fits = labelWidth <= room
            let title = Text(showsPageTitle ? tab.title : domain)
                .font(.app(12, isActive ? .medium : .regular))
                .foregroundStyle(isActive ? Palette.text : Palette.textSecondary)
                .lineLimit(1)
                .fixedSize()
            let icon = Favicon(host: tab.webView.url?.host ?? "", iconURL: tab.iconURL, size: 14,
                               tint: Palette.textSecondary)
            ZStack(alignment: .leading) {
                // Measured at natural width, never shown.
                HStack(spacing: 6) { icon; title; badge; chevron }
                    .hidden()
                    .background(GeometryReader { geo in
                        Color.clear.preference(key: StackLabelWidthKey.self, value: geo.size.width)
                    })
                if fits {
                    HStack(spacing: 6) { icon; title; badge; chevron }
                        .offset(x: 10 + (room - labelWidth) / 2)
                } else {
                    // Favicon 14, three 6pt gaps, badge ≈ 19 (one digit), chevron ≈ 7.
                    let titleRoom = max(room - 14 - 18 - 19 - 7, 0)
                    HStack(spacing: 6) {
                        icon
                        title
                            .frame(width: titleRoom, alignment: .leading)
                            .mask {
                                LinearGradient(
                                    stops: [.init(color: .black, location: 0),
                                            .init(color: .black, location: max(0, (titleRoom - 16) / max(titleRoom, 1))),
                                            .init(color: .clear, location: 1)],
                                    startPoint: .leading, endPoint: .trailing)
                            }
                            .clipped()
                        badge
                        chevron
                    }
                    .offset(x: 10)
                }
            }
            .onPreferenceChange(StackLabelWidthKey.self) { labelWidth = $0 }
            .frame(width: width, height: 28, alignment: .leading)
            .clipped()
        }
    }
}

private struct StackLabelWidthKey: PreferenceKey {
    static let defaultValue: CGFloat = 0
    static func reduce(value: inout CGFloat, nextValue: () -> CGFloat) { value = max(value, nextValue()) }
}

/// A stack's pages, in Krackn's own menu rather than the system popover:
/// a glass panel that unfolds from the stack, with hover, keyboard control
/// (↑ ↓ Return Esc) and a right-click menu on every page.
struct TabStackDropdown: View {
    static let width: CGFloat = 300
    let tabs: [BrowserTab]
    let domain: String
    @ObservedObject var model: BrowserModel
    let dismiss: () -> Void
    /// Whether a dragged file is anywhere over the dropdown.
    var onDragPresence: (Bool) -> Void = { _ in }
    @State private var highlighted: UUID?
    @FocusState private var focused: Bool
    @State private var panelTargeted = false
    @State private var rowTargets: Set<UUID> = []
    @State private var springLoad: Task<Void, Never>?
    /// Drag-to-reorder inside the list: the row being dragged, where it
    /// started, and how far it's drawn from its current slot.
    @State private var draggingRow: UUID?
    @State private var rowStart = 0
    @State private var rowOffset: CGFloat = 0
    /// Row height plus the gap between rows.
    static let rowPitch: CGFloat = 31

    var body: some View {
        VStack(alignment: .leading, spacing: 2) {
            HStack(spacing: 7) {
                Favicon(host: tabs.first?.webView.url?.host ?? "", iconURL: tabs.first?.iconURL,
                        size: 13, tint: Palette.textSecondary)
                Text(domain)
                    .font(.app(12, .semibold))
                    .foregroundStyle(Palette.text)
                Spacer()
                Text("\(tabs.count) tabs")
                    .font(.app(11))
                    .foregroundStyle(Palette.textSecondary)
            }
            .padding(.horizontal, 10)
            .padding(.top, 6)
            .padding(.bottom, 4)

            ScrollView {
                VStack(spacing: 1) {
                    ForEach(tabs) { tab in
                        DropdownRow(tab: tab, model: model,
                                    isCurrent: tab.id == model.activeID,
                                    isHighlighted: tab.id == highlighted || tab.id == draggingRow,
                                    isDragging: tab.id == draggingRow) {
                            model.activate(tab)
                            dismiss()
                        } onClose: {
                            withAnimation(Motion.standard) { model.close(tab) }
                            if tabs.count <= 2 { dismiss() }
                        } onDrag: { dy in
                            dragRow(tab, dy)
                        } onDragEnd: {
                            withAnimation(Motion.standard) {
                                rowOffset = 0
                                draggingRow = nil
                            }
                            model.isDraggingTab = false
                            model.scheduleSessionSave()
                        }
                        .offset(y: tab.id == draggingRow ? rowOffset : 0)
                        .zIndex(tab.id == draggingRow ? 1 : 0)
                        .onHover { if $0, draggingRow == nil { highlighted = tab.id } }
                        // Held over a page while dragging a file: switch to
                        // it and get out of the way, so the drop lands there.
                        .onDrop(of: DropRouter.hoverTypes, isTargeted: Binding(
                            get: { rowTargets.contains(tab.id) },
                            set: { rowTarget(tab, $0) })) { _ in false }
                    }
                }
            }
            // As tall as its rows, up to a limit — a bare max height made the
            // scroll view take all of it, leaving a gap under a short list.
            .frame(height: min(CGFloat(tabs.count) * Self.rowPitch - 1, 360))
            .animation(Motion.quick, value: tabs.map(\.id))
            .scrollIndicators(.never)

            Rectangle().fill(Palette.separator).frame(height: 1).padding(.horizontal, 6).padding(.vertical, 3)
            DropdownAction(title: "Close All \(tabs.count) Tabs", icon: "xmark.square") {
                withAnimation(Motion.standard) { tabs.forEach { model.close($0) } }
                dismiss()
            }
        }
        .padding(5)
        .frame(width: Self.width)
        .glass(radius: 12, material: .regularMaterial, tint: 0.35)
        .focusable()
        .focused($focused)
        .focusEffectDisabled()
        .onKeyPress(.downArrow) { move(1); return .handled }
        .onKeyPress(.upArrow) { move(-1); return .handled }
        .onKeyPress(.return) {
            if let id = highlighted, let tab = tabs.first(where: { $0.id == id }) {
                model.activate(tab)
                dismiss()
            }
            return .handled
        }
        .onKeyPress(.escape) { dismiss(); return .handled }
        .onDrop(of: DropRouter.hoverTypes, isTargeted: $panelTargeted) { _ in false }
        .onChange(of: panelTargeted || !rowTargets.isEmpty) { _, inside in onDragPresence(inside) }
        .onDisappear { springLoad?.cancel() }
        .onAppear {
            highlighted = model.activeID.flatMap { id in tabs.contains { $0.id == id } ? id : nil }
                ?? tabs.first?.id
            DispatchQueue.main.async { focused = true }
        }
    }

    /// A row dragged `dy` points from where it was picked up. Rows are a
    /// fixed pitch, so its slot is the start plus whole rows travelled; the
    /// row is drawn under the cursor and the others make way as it passes.
    private func dragRow(_ tab: BrowserTab, _ dy: CGFloat) {
        if draggingRow != tab.id {
            guard let start = tabs.firstIndex(where: { $0.id == tab.id }) else { return }
            // A sorted arrangement would put the page straight back: freeze
            // the order on screen and switch to manual, as the strip does.
            if model.organization.dragSwitchesToManual {
                model.freezeDisplayedOrder()
                UserDefaults.standard.set(TabOrganization.manual.rawValue,
                                          forKey: AppSettings.Key.tabOrganization)
                model.post("Tabs are now in manual order. Change it in Settings › Tabs.")
            }
            draggingRow = tab.id
            rowStart = start
            model.isDraggingTab = true
        }
        let target = min(max(rowStart + Int((dy / Self.rowPitch).rounded()), 0), tabs.count - 1)
        if let current = tabs.firstIndex(where: { $0.id == tab.id }), current != target {
            withAnimation(Motion.quick) { model.move(tab.id, within: tabs.map(\.id), to: target) }
        }
        rowOffset = dy - CGFloat(target - rowStart) * Self.rowPitch
    }

    private func rowTarget(_ tab: BrowserTab, _ targeted: Bool) {
        if targeted { rowTargets.insert(tab.id) } else { rowTargets.remove(tab.id) }
        springLoad?.cancel()
        guard targeted else { return }
        highlighted = tab.id
        springLoad = Task {
            // Shorter than the strip's: the list is already a deliberate stop.
            try? await Task.sleep(for: .milliseconds(350))
            guard !Task.isCancelled else { return }
            await MainActor.run {
                model.activate(tab)
                dismiss()
            }
        }
    }

    private func move(_ delta: Int) {
        let ids = tabs.map(\.id)
        guard !ids.isEmpty else { return }
        let current = highlighted.flatMap { ids.firstIndex(of: $0) } ?? -1
        highlighted = ids[min(max(current + delta, 0), ids.count - 1)]
    }
}

private struct DropdownRow: View {
    @ObservedObject var tab: BrowserTab
    let model: BrowserModel
    let isCurrent: Bool
    let isHighlighted: Bool
    var isDragging = false
    let onSelect: () -> Void
    let onClose: () -> Void
    let onDrag: (CGFloat) -> Void
    let onDragEnd: () -> Void
    @State private var closeHover = false
    @State private var dragged = false

    var body: some View {
        HStack(spacing: 9) {
            Favicon(host: tab.webView.url?.host ?? "", iconURL: tab.iconURL, size: 14,
                    tint: Palette.textSecondary)
            Text(tab.title)
                .font(.app(13, isCurrent ? .medium : .regular))
                .foregroundStyle(Palette.text)
                .lineLimit(1)
            Spacer(minLength: 6)
            if isCurrent {
                Circle().fill(Palette.accentText).frame(width: 5, height: 5)
                    .help("The page you're on")
            }
            Button(action: onClose) {
                Image(systemName: "xmark")
                    .font(.system(size: 8, weight: .bold))
                    .frame(width: 18, height: 18)
                    .background(Circle().fill(closeHover ? Palette.text.opacity(0.1) : .clear))
                    .contentShape(Circle())
            }
            .buttonStyle(.plain)
            .foregroundStyle(Palette.textSecondary)
            .onHover { closeHover = $0 }
            .opacity(isHighlighted ? 1 : 0)
            .allowsHitTesting(isHighlighted)
            .help("Close tab")
        }
        .padding(.horizontal, 8)
        .frame(height: 30)
        .background(RoundedRectangle(cornerRadius: 7, style: .continuous)
            .fill(isHighlighted ? Palette.text.opacity(0.08) : .clear)
            .shadow(color: .black.opacity(isDragging ? 0.18 : 0), radius: 6, y: 2))
        .contentShape(Rectangle())
        // One gesture owns both the click and the drag. A separate tap would
        // also fire when a drag ends, opening the page you just moved; and it
        // has to be simultaneous, or the scroll view around the rows wins.
        // Measured in window space: the row itself moves as it's reordered,
        // so in its own space the distance shrank by a row at every swap.
        .simultaneousGesture(DragGesture(minimumDistance: 0, coordinateSpace: .global)
            .onChanged { value in
                guard dragged || abs(value.translation.height) > 4 else { return }
                dragged = true
                onDrag(value.translation.height)
            }
            .onEnded { _ in
                if dragged {
                    dragged = false
                    onDragEnd()
                } else if !closeHover {
                    onSelect()
                }
            })
        .contextMenu { TabMenuItems(tab: tab, model: model) }
        .animation(Motion.hover, value: isHighlighted)
    }
}

private struct DropdownAction: View {
    let title: String
    let icon: String
    let action: () -> Void
    @State private var hovering = false

    var body: some View {
        HStack(spacing: 9) {
            Image(systemName: icon).font(.system(size: 12)).frame(width: 14)
            Text(title).font(.app(12.5))
            Spacer()
        }
        .foregroundStyle(Palette.textSecondary)
        .padding(.horizontal, 8)
        .frame(height: 28)
        .background(RoundedRectangle(cornerRadius: 7, style: .continuous)
            .fill(hovering ? Palette.text.opacity(0.08) : .clear))
        .contentShape(Rectangle())
        .onTapGesture(perform: action)
        .onHover { hovering = $0 }
        .animation(Motion.hover, value: hovering)
    }
}
