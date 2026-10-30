import SwiftUI
import AppKit

/// The sidebar.
///
/// It holds only what you move between: the browser, the Toolbox, the last
/// three features you used, and any popped-out windows you've minimized —
/// with Settings at the foot. Everything else is one click into the Toolbox.
/// A sidebar listing all ten sections was a table of contents, not navigation.
///
/// Drawn as an inset pane with continuous corners, the way macOS draws a
/// sidebar now: it floats a few points inside the window rather than running
/// edge to edge, and the traffic lights sit inside it. The same view serves
/// low-profile mode, sliding out over the content when the cursor reaches the
/// window's edge.
struct Sidebar: View {
    @Binding var route: Route
    /// Pinned in place (true) or tucked away until the cursor asks for it.
    @Binding var pinned: Bool
    @ObservedObject var recents: RecentFeatures
    @ObservedObject var panels: PanelStore
    /// True when this copy is the one sliding out over the content.
    var floating = false
    let onOpen: (PanelContent) -> Void
    let onReselect: (Route) -> Void
    let onRestore: (FloatingPanel) -> Void
    let onSettings: () -> Void
    let onDropFiles: (PanelContent, [URL]) -> Void
    let onTearOff: (PanelContent, CGPoint) -> Void
    /// Reports where the "Minimized" rows are, in panel-host coordinates, so a
    /// minimizing panel can shrink into exactly that spot.
    let onMinimizeTarget: (CGPoint) -> Void

    static let width: CGFloat = 196
    static let inset: CGFloat = 8

    @Namespace private var plate

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            header
            ScrollView {
                VStack(alignment: .leading, spacing: 1) {
                    row(.browser, icon: "globe", title: "Browser")
                    row(.toolbox, icon: "square.grid.2x2", title: "Toolbox")

                    if !recents.items.isEmpty {
                        heading("Recent")
                        ForEach(recents.items) { item in
                            featureRow(item)
                                .transition(.asymmetric(
                                    insertion: .move(edge: .top).combined(with: .opacity),
                                    removal: .opacity))
                        }
                    }

                    if !panels.minimized.isEmpty {
                        heading("Minimized")
                            .background(GeometryReader { geo in
                                Color.clear.preference(
                                    key: MinimizeTargetKey.self,
                                    value: geo.frame(in: .named(PanelHost.space)))
                            })
                        ForEach(panels.minimized) { panel in
                            minimizedRow(panel)
                                .transition(.asymmetric(
                                    insertion: .scale(scale: 0.6, anchor: .leading)
                                        .combined(with: .opacity),
                                    removal: .opacity))
                        }
                    }
                }
                .padding(.horizontal, 8)
                .animation(Motion.standard, value: recents.items)
                .animation(Motion.standard, value: panels.minimized.map(\.id))
            }
            .scrollIndicators(.never)
            .onPreferenceChange(MinimizeTargetKey.self) { frame in
                // Aim slightly below the heading, where the new row appears.
                onMinimizeTarget(CGPoint(x: frame.minX + 28, y: frame.maxY + 14))
            }

            Spacer(minLength: 6)
            settingsRow
                .padding(.horizontal, 8)
                .padding(.bottom, 8)
        }
        .frame(width: Self.width)
        .frame(maxHeight: .infinity)
        .glass(radius: 12, tint: floating ? 0.45 : 0.7, shadow: floating)
    }

    // MARK: Pieces

    /// The traffic lights live in this band; the pin toggle sits opposite them.
    private var header: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(spacing: 0) {
                // The drag strip *is* the spacer: a WindowDragArea placed as a
                // background never receives clicks (see CLAUDE.md).
                WindowDragArea().frame(maxWidth: .infinity).frame(height: 28)
                SidebarIconButton(system: "sidebar.left",
                                  help: pinned ? "Hide sidebar (⌃⌘S)" : "Keep sidebar open (⌃⌘S)") {
                    withAnimation(Motion.surface) { pinned.toggle() }
                }
            }
            // Same band as the traffic lights, which `WindowChrome` moves in
            // to sit inside this pane — so the toggle lines up with them.
            .frame(height: 28)
            .padding(.top, 2)
            .padding(.trailing, 6)

            HStack(spacing: 8) {
                KracknLogo(size: 18)
                Text("Krackn")
                    .font(.app(13.5, .semibold))
                    .foregroundStyle(Palette.text)
            }
            .padding(.leading, 14)
            .padding(.bottom, 6)
        }
    }

    private func heading(_ title: String) -> some View {
        Text(title)
            .font(.app(11, .semibold))
            .foregroundStyle(Palette.textSecondary.opacity(0.85))
            .padding(.leading, 10)
            .padding(.top, 14)
            .padding(.bottom, 4)
            .frame(maxWidth: .infinity, alignment: .leading)
    }

    private func row(_ target: Route, icon: String, title: String) -> some View {
        SidebarRow(icon: icon, title: title, isSelected: route == target, plate: plate) {
            if route == target { onReselect(target) } else { route = target }
        }
    }

    private func featureRow(_ item: PanelContent) -> some View {
        // The tear-off modifier owns the click here: a row that also handled
        // its own tap would fire twice, opening the feature *and* a window.
        SidebarRow(icon: item.icon, title: item.title,
                   isSelected: route == .feature(item), plate: plate,
                   handlesTap: false) {}
        // Same as a Toolbox card: click opens it here, hold and drag pops it
        // out into a window.
        .tearable(item, onTap: { onOpen(item) }, onTearOff: onTearOff)
        .dropDestination(accepting: item.acceptsFiles) { urls in onDropFiles(item, urls) }
        .contextMenu {
            Button("Open in Window") {
                onTearOff(item, CGPoint(x: Self.width + 360, y: 140))
            }
            Divider()
            Button("Remove from Sidebar") { recents.remove(item) }
        }
    }

    private func minimizedRow(_ panel: FloatingPanel) -> some View {
        SidebarRow(icon: panel.content.icon, title: panel.content.title,
                   isSelected: false, plate: plate, trailing: "macwindow") {
            onRestore(panel)
        }
        .contextMenu {
            Button("Restore") { onRestore(panel) }
            Button("Close") { withAnimation(Motion.standard) { panels.close(panel.id) } }
        }
    }

    private var settingsRow: some View {
        SidebarRow(icon: "gearshape", title: "Settings", isSelected: false, plate: plate,
                   action: onSettings)
    }
}

/// Collects the "Minimized" heading's frame for the minimize animation.
private struct MinimizeTargetKey: PreferenceKey {
    static let defaultValue: CGRect = .zero
    static func reduce(value: inout CGRect, nextValue: () -> CGRect) {
        let next = nextValue()
        if next != .zero { value = next }
    }
}

/// One sidebar entry. The selection plate is shared between rows through a
/// matched geometry effect, so changing section slides one plate to the new
/// row instead of fading one out and another in.
struct SidebarRow: View {
    let icon: String
    let title: String
    let isSelected: Bool
    let plate: Namespace.ID
    var trailing: String?
    var handlesTap = true
    let action: () -> Void

    @State private var hovering = false

    var body: some View {
        HStack(spacing: 9) {
            Image(systemName: icon)
                .font(.system(size: 13.5, weight: .regular))
                .foregroundStyle(isSelected ? Palette.accentText : Palette.textSecondary)
                .frame(width: 20)
            Text(title)
                .font(.app(13, .regular))
                .foregroundStyle(Palette.text)
                .lineLimit(1)
            Spacer(minLength: 0)
            if let trailing {
                Image(systemName: trailing)
                    .font(.system(size: 10.5))
                    .foregroundStyle(Palette.textSecondary.opacity(0.7))
            }
        }
        .padding(.horizontal, 9)
        .frame(height: 30)
        .background {
            let shape = RoundedRectangle(cornerRadius: Metrics.controlRadius, style: .continuous)
            if isSelected {
                shape.fill(Palette.selection.opacity(0.85))
                    .matchedGeometryEffect(id: "plate", in: plate)
            } else if hovering {
                shape.fill(Palette.text.opacity(0.05))
            }
        }
        .contentShape(Rectangle())
        .onHover { hovering = $0 }
        .onTapGesture { if handlesTap { withAnimation(Motion.standard) { action() } } }
        .animation(Motion.hover, value: hovering)
        .help(title)
    }
}

/// Sidebar header glyph: borderless, a soft fill under the pointer.
struct SidebarIconButton: View {
    let system: String
    let help: String
    let action: () -> Void
    @State private var hovering = false

    var body: some View {
        Button(action: action) {
            Image(systemName: system)
                .font(.system(size: 13, weight: .regular))
                .foregroundStyle(Palette.textSecondary)
                .frame(width: 28, height: 24)
                .background(RoundedRectangle(cornerRadius: 6, style: .continuous)
                    .fill(hovering ? Palette.text.opacity(0.07) : .clear))
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .onHover { hovering = $0 }
        .animation(Motion.hover, value: hovering)
        .help(help)
    }
}

extension PanelContent {
    /// Features that do something with a dropped file.
    var acceptsFiles: Bool {
        switch self {
        case .section(let s): return s == .viewer || s == .convert
        case .tool(let t): return t == .bgRemove
        }
    }
}

private struct FileDropModifier: ViewModifier {
    let accepting: Bool
    let onDrop: ([URL]) -> Void
    @State private var targeted = false

    func body(content: Content) -> some View {
        content
            .overlay {
                RoundedRectangle(cornerRadius: Metrics.controlRadius, style: .continuous)
                    .strokeBorder(Palette.accent.opacity(targeted ? 1 : 0), lineWidth: 1.5)
                    .allowsHitTesting(false)
            }
            .animation(Motion.hover, value: targeted)
            // Empty type list when this row can't take files — never `.item`,
            // which would swallow right-clicks (see CLAUDE.md).
            .onDrop(of: accepting ? DropRouter.acceptedTypes : [], isTargeted: $targeted) { providers in
                guard accepting else { return false }
                Task {
                    let urls = await DropRouter.resolve(providers)
                    guard !urls.isEmpty else { return }
                    await MainActor.run { onDrop(urls) }
                }
                return true
            }
    }
}

extension View {
    func dropDestination(accepting: Bool, onDrop: @escaping ([URL]) -> Void) -> some View {
        modifier(FileDropModifier(accepting: accepting, onDrop: onDrop))
    }
}

// MARK: - Edge reveal

/// Watches the cursor so the tucked-away sidebar can slide out when it reaches
/// the window's leading edge, and tuck back once it leaves.
///
/// A local event monitor rather than a SwiftUI hover zone: an invisible strip
/// over the left edge would sit on top of the web view and swallow its
/// clicks, while a monitor only observes. It needs the window to deliver
/// mouse-moved events at all, which `WindowChrome` turns on.
@MainActor
final class EdgeRevealMonitor: ObservableObject {
    @Published var revealed = false
    /// How close to the edge, in points, counts as "asking for the sidebar".
    static let edge: CGFloat = 6
    private var monitor: Any?
    private var pending: Task<Void, Never>?
    var isEnabled = false {
        didSet { if !isEnabled { hide(immediately: true) } }
    }
    /// The window this sidebar belongs to. The monitor sees every window's
    /// events; without this, nearing the edge of one window would slide the
    /// sidebar out in all of them.
    weak var window: NSWindow?

    func start() {
        guard monitor == nil else { return }
        monitor = NSEvent.addLocalMonitorForEvents(matching: [.mouseMoved, .leftMouseDragged]) {
            [weak self] event in
            self?.track(event)
            return event
        }
    }

    func stop() {
        if let monitor { NSEvent.removeMonitor(monitor) }
        monitor = nil
    }

    /// Shows the sidebar for a moment without the cursor — used when a panel
    /// is minimized into it while it's tucked away, so you see where it went.
    func peek(for seconds: Double = 1.4) {
        guard isEnabled else { return }
        withAnimation(Motion.surface) { revealed = true }
        pending?.cancel()
        pending = Task { [weak self] in
            try? await Task.sleep(for: .seconds(seconds))
            guard !Task.isCancelled else { return }
            self?.hide(immediately: false)
        }
    }

    private func track(_ event: NSEvent) {
        guard isEnabled, let window = event.window, window === self.window else { return }
        let x = event.locationInWindow.x
        let y = window.contentLayoutRect.height - event.locationInWindow.y
        if !revealed {
            // A short dwell, so sweeping past the edge on the way to the Dock
            // or another window doesn't fling the sidebar out.
            guard x <= Self.edge, y > 0 else { pending?.cancel(); pending = nil; return }
            guard pending == nil else { return }
            pending = Task { [weak self] in
                try? await Task.sleep(for: .milliseconds(70))
                guard !Task.isCancelled else { return }
                withAnimation(Motion.surface) { self?.revealed = true }
                self?.pending = nil
            }
        } else if x > Sidebar.width + Sidebar.inset + 36 {
            hide(immediately: false)
        } else {
            pending?.cancel()
            pending = nil
        }
    }

    private func hide(immediately: Bool) {
        pending?.cancel()
        pending = nil
        guard revealed else { return }
        if immediately {
            revealed = false
        } else {
            withAnimation(Motion.standard) { revealed = false }
        }
    }
}
