import SwiftUI

/// What a floating panel is showing. Either a whole section from the dock or a
/// single tool from the Toolbox.
enum PanelContent: Hashable, Identifiable {
    case section(Section)
    case tool(UtilityTool)

    var id: String {
        switch self {
        case .section(let s): return "section:\(s.rawValue)"
        case .tool(let t): return "tool:\(t.rawValue)"
        }
    }

    /// Round-trips `id`, so the sidebar's recents can be stored as strings.
    init?(id: String) {
        if id.hasPrefix("section:"), let s = Section(rawValue: String(id.dropFirst(8))) {
            self = .section(s)
        } else if id.hasPrefix("tool:"), let t = UtilityTool(rawValue: String(id.dropFirst(5))) {
            self = .tool(t)
        } else {
            return nil
        }
    }

    var title: String {
        switch self {
        case .section(let s): return s.rawValue
        case .tool(let t): return t.rawValue
        }
    }

    var icon: String {
        switch self {
        case .section(let s): return s.icon
        case .tool(let t): return t.icon
        }
    }

    /// The browser is deliberately not poppable. It owns long-lived web views
    /// that are re-parented on every section switch, and a second live copy of
    /// that view hierarchy would fight the main one over which container owns
    /// each `WKWebView`.
    static func isTearable(_ section: Section) -> Bool {
        section != .browser && section != .utilities && section != .settings
    }

    /// Panels are sized to what they hold. A one-size-fits-all box either
    /// wastes half its area on the UUID generator or crops the graphing
    /// calculator's plot.
    var preferredSize: CGSize {
        switch self {
        case .section(let section):
            switch section {
            case .viewer, .convert: return CGSize(width: 520, height: 580)
            case .settings, .security, .network: return CGSize(width: 540, height: 620)
            case .utilities: return CGSize(width: 520, height: 600)
            default: return CGSize(width: 480, height: 540)
            }
        case .tool(let tool):
            switch tool {
            // Needs room for the expression list beside the plot.
            case .graphing: return CGSize(width: 640, height: 560)
            case .bgRemove, .qr: return CGSize(width: 500, height: 620)
            case .regex, .jwt, .json, .textStats, .hash, .hmac, .base64:
                return CGSize(width: 520, height: 580)
            case .calculator: return CGSize(width: 520, height: 540)
            default: return CGSize(width: 460, height: 500)
            }
        }
    }
}

/// Marks a view as being hosted inside a floating panel.
///
/// The tools were built for a full window: a 42pt page title, 50pt of top
/// padding to clear the traffic lights, controls pinned to 320pt. All of that
/// is wrong at panel size, and the fix isn't a separate cut-down view per tool
/// — it's letting each one read its own context and tighten up.
private struct FloatingPanelKey: EnvironmentKey {
    static let defaultValue = false
}

extension EnvironmentValues {
    var inFloatingPanel: Bool {
        get { self[FloatingPanelKey.self] }
        set { self[FloatingPanelKey.self] = newValue }
    }
}

/// One floating panel's position and size, in the main window's coordinate
/// space.
struct FloatingPanel: Identifiable, Equatable {
    let id = UUID()
    var content: PanelContent
    var origin: CGPoint
    var size: CGSize
    /// Minimized panels stay in the view hierarchy, shrunk into the sidebar
    /// and invisible, so a half-typed regex or a loaded file is still there
    /// when the panel comes back. Removing the view would throw that away.
    var isMinimized = false

    init(content: PanelContent, origin: CGPoint) {
        self.content = content
        self.origin = origin
        self.size = content.preferredSize
    }

    var frame: CGRect { CGRect(origin: origin, size: size) }
}

/// Tracks the open panels.
///
/// At most three on screen at once: they live inside the main window and are
/// kept from overlapping, so a fourth would have nowhere to go. Minimized
/// panels don't count — they're parked in the sidebar.
@MainActor
final class PanelStore: ObservableObject {
    static let maximumVisible = 3

    @Published private(set) var panels: [FloatingPanel] = []
    /// The panel drawn on top, and the one a drag is currently moving.
    @Published var frontID: UUID?
    var visible: [FloatingPanel] { panels.filter { !$0.isMinimized } }
    var minimized: [FloatingPanel] { panels.filter(\.isMinimized) }
    var isFull: Bool { visible.count >= Self.maximumVisible }

    /// Opens `content` at `point`, or brings an existing panel for the same
    /// content back — restoring it if it was minimized — rather than opening
    /// a duplicate.
    @discardableResult
    func open(_ content: PanelContent, at point: CGPoint, in bounds: CGSize) -> Bool {
        if let existing = panels.first(where: { $0.content == content }) {
            if existing.isMinimized { return restore(existing.id, in: bounds) }
            bringToFront(existing.id)
            return true
        }
        guard !isFull else { return false }

        var panel = FloatingPanel(content: content, origin: .zero)
        // A panel can't be bigger than the window it lives in.
        panel.size = CGSize(width: min(panel.size.width, max(bounds.width - 80, 320)),
                            height: min(panel.size.height, max(bounds.height - 80, 300)))
        // Centred under the cursor, clamped so it can't open half-outside.
        panel.origin = clamped(
            CGPoint(x: point.x - panel.size.width / 2, y: point.y - 18),
            size: panel.size, in: bounds)
        panels.append(panel)
        frontID = panel.id
        settle(around: panel.id, in: bounds)
        return true
    }

    func close(_ id: UUID) {
        panels.removeAll { $0.id == id }
        if frontID == id { frontID = visible.last?.id }
    }

    func closeAll() {
        panels = []
        frontID = nil
    }

    func minimize(_ id: UUID) {
        guard let index = panels.firstIndex(where: { $0.id == id }) else { return }
        panels[index].isMinimized = true
        if frontID == id { frontID = visible.last?.id }
    }

    /// Brings a minimized panel back where it was, nudging anything that has
    /// since moved into its spot.
    @discardableResult
    func restore(_ id: UUID, in bounds: CGSize) -> Bool {
        guard let index = panels.firstIndex(where: { $0.id == id }) else { return false }
        guard panels[index].isMinimized else { bringToFront(id); return true }
        guard !isFull else { return false }
        panels[index].isMinimized = false
        bringToFront(id)
        settle(around: id, in: bounds)
        return true
    }

    func bringToFront(_ id: UUID) {
        guard let index = panels.firstIndex(where: { $0.id == id }) else { return }
        let panel = panels.remove(at: index)
        panels.append(panel)
        frontID = id
    }

    // MARK: Dragging

    func beginDrag(_ id: UUID) { bringToFront(id) }

    /// Panels make room only once the dragged one is let go. Pushing them
    /// aside on every frame of the drag had them sliding around while you
    /// were still deciding where to put it.
    func endDrag(_ id: UUID, at origin: CGPoint, in bounds: CGSize) {
        if let index = panels.firstIndex(where: { $0.id == id }) {
            panels[index].origin = clamped(origin, size: panels[index].size, in: bounds)
        }
        // Next turn, outside the caller's no-animation transaction: the
        // released panel lands instantly, the ones it displaced glide away.
        DispatchQueue.main.async { [weak self] in self?.settle(around: id, in: bounds) }
    }

    /// Keeps panels inside the window. Re-run on every resize, so shrinking
    /// the window drags them back in rather than stranding them past the edge.
    func clampAll(in bounds: CGSize) {
        for index in panels.indices {
            panels[index].origin = clamped(panels[index].origin,
                                           size: panels[index].size, in: bounds)
        }
    }

    /// Leaves a sliver of the title bar reachable at every edge, so a panel
    /// can always be grabbed again.
    func clamped(_ point: CGPoint, size: CGSize, in bounds: CGSize) -> CGPoint {
        let margin: CGFloat = 60
        let maxX = max(bounds.width - margin, 0)
        let maxY = max(bounds.height - 40, 0)
        return CGPoint(x: min(max(point.x, margin - size.width), maxX),
                       y: min(max(point.y, 6), maxY))
    }

    /// Pushes every other visible panel clear of `id`.
    private func settle(around id: UUID, in bounds: CGSize) {
        let shown = visible
        guard let fixed = shown.firstIndex(where: { $0.id == id }) else { return }
        let resolved = PanelLayout.resolve(
            fixed: fixed, frames: shown.map(\.frame),
            in: CGRect(origin: .zero, size: bounds), allowMovingFixed: true)
        withAnimation(Motion.yield) {
            for (panel, frame) in zip(shown, resolved) where panel.origin != frame.origin {
                if let index = panels.firstIndex(where: { $0.id == panel.id }) {
                    panels[index].origin = frame.origin
                }
            }
        }
    }
}

/// The chrome around a popped-out tool.
///
/// Deliberately *not* an `NSWindow`. A real window would live in Mission
/// Control, appear in the window menu, and drift away from the app when the
/// main window moves. Drawn inside the main window's own hierarchy, it moves
/// with the app for free and can never be separated from it.
struct FloatingPanelView<Content: View>: View {
    let panel: FloatingPanel
    let isFront: Bool
    let bounds: CGSize
    /// Where a minimized panel shrinks to — its row in the sidebar.
    let minimizeTarget: CGPoint
    let store: PanelStore
    let onClose: () -> Void
    let onMinimize: () -> Void
    @ViewBuilder var content: Content

    /// The live drag, applied on top of the stored origin. Kept local so the
    /// panel under the cursor follows it exactly, with no animation, while
    /// the store animates everything it pushes aside.
    @State private var drag: CGSize = .zero
    @State private var dragging = false
    @State private var hoveringControls = false

    private var liveOrigin: CGPoint {
        store.clamped(CGPoint(x: panel.origin.x + drag.width, y: panel.origin.y + drag.height),
                      size: panel.size, in: bounds)
    }

    var body: some View {
        let shape = RoundedRectangle(cornerRadius: 12, style: .continuous)
        let center = CGPoint(x: liveOrigin.x + panel.size.width / 2,
                             y: liveOrigin.y + panel.size.height / 2)
        VStack(spacing: 0) {
            titleBar
            content
                .frame(maxWidth: .infinity, maxHeight: .infinity)
                .background(Palette.background)
                .clipped()
        }
        .frame(width: panel.size.width, height: panel.size.height)
        .background(Palette.background)
        .clipShape(shape)
        .overlay(shape.strokeBorder(Palette.separator, lineWidth: 1).allowsHitTesting(false))
        // Two shadows, as macOS draws a window's: a wide soft one for depth
        // and a tight one that defines the edge. The front panel sits higher.
        .shadow(color: .black.opacity(isFront ? 0.22 : 0.12),
                radius: isFront ? 28 : 16, y: isFront ? 14 : 7)
        .shadow(color: .black.opacity(0.08), radius: 1.5, y: 0.5)
        .scaleEffect(dragging ? 1.008 : 1)
        // Minimizing shrinks the panel into its sidebar row: scale toward the
        // target while sliding there, fading and softening on the way.
        .scaleEffect(panel.isMinimized ? 0.06 : 1)
        .offset(panel.isMinimized
                ? CGSize(width: minimizeTarget.x - center.x, height: minimizeTarget.y - center.y)
                : .zero)
        .opacity(panel.isMinimized ? 0 : 1)
        .blur(radius: panel.isMinimized ? 6 : 0)
        .allowsHitTesting(!panel.isMinimized)
        .position(center)
        .animation(Motion.quick, value: dragging)
        .simultaneousGesture(TapGesture().onEnded { store.bringToFront(panel.id) })
    }

    private var titleBar: some View {
        HStack(spacing: 8) {
            Image(systemName: panel.content.icon)
                .font(.system(size: 11.5, weight: .regular))
                .foregroundStyle(isFront ? Palette.accentText : Palette.textSecondary)
            Text(panel.content.title)
                .font(.app(12.5, .medium))
                .foregroundStyle(isFront ? Palette.text : Palette.textSecondary)
                .lineLimit(1)
            Spacer(minLength: 8)
            HStack(spacing: 4) {
                PanelControl(system: "minus", help: "Minimize to sidebar", action: onMinimize)
                PanelControl(system: "xmark", help: "Close", action: onClose)
            }
        }
        .padding(.leading, 12)
        .padding(.trailing, 8)
        .frame(height: 34)
        .background(.bar)
        .background(Palette.tint.opacity(0.5))
        .overlay(alignment: .bottom) {
            Rectangle().fill(Palette.separator).frame(height: 1)
        }
        .contentShape(Rectangle())
        .onTapGesture(count: 2, perform: onMinimize)
        // The title bar is the drag handle, the way a real window's is.
        .gesture(
            DragGesture(minimumDistance: 1, coordinateSpace: .named(PanelHost.space))
                .onChanged { value in
                    if !dragging {
                        dragging = true
                        store.beginDrag(panel.id)
                    }
                    drag = value.translation
                }
                .onEnded { _ in
                    let final = liveOrigin
                    // Commit and reset in one un-animated transaction, so the
                    // panel doesn't jump back and forward between them.
                    var transaction = Transaction()
                    transaction.disablesAnimations = true
                    withTransaction(transaction) {
                        drag = .zero
                        store.endDrag(panel.id, at: final, in: bounds)
                    }

                    dragging = false
                }
        )
    }
}

/// The minimize and close buttons in a panel's title bar: a bare glyph that
/// gains a soft disc under the pointer.
private struct PanelControl: View {
    let system: String
    let help: String
    let action: () -> Void
    @State private var hovering = false

    var body: some View {
        Button(action: action) {
            Image(systemName: system)
                .font(.system(size: 9.5, weight: .semibold))
                .foregroundStyle(hovering ? Palette.text : Palette.textSecondary)
                .frame(width: 22, height: 22)
                .background(Circle().fill(hovering ? Palette.controlFill : .clear))
                .contentShape(Circle())
        }
        .buttonStyle(.plain)
        .onHover { hovering = $0 }
        .animation(Motion.hover, value: hovering)
        .help(help)
    }
}

enum PanelHost {
    /// Coordinate space the panels position themselves in — the content area
    /// of the main window.
    static let space = "krackn.panelHost"
}

// MARK: - Tear-off gesture

/// Makes a dock row or tool card tearable into a floating panel.
///
/// Long-press *then* drag, rather than drag alone: these rows are also plain
/// buttons and, in the dock's case, file drop targets, so an immediate drag
/// would make every slightly-imprecise click feel like it did something
/// unexpected. Holding for a beat is an unambiguous "I mean this one".
struct TearOffModifier: ViewModifier {
    let content: PanelContent
    let isEnabled: Bool
    /// The ordinary click action — navigating to the section or opening the
    /// tool. Owned by this modifier rather than by a `Button` underneath it:
    /// with a `Button` still in place, a completed tear-off *also* fired the
    /// button on mouse-up, so pulling a panel out navigated the main window to
    /// that page as well. One gesture decides which of the two happened.
    let onTap: () -> Void
    /// Reports the tear-off in the panel host's coordinate space.
    let onTearOff: (PanelContent, CGPoint) -> Void

    @State private var armed = false
    @State private var cancelled = false
    @State private var pressTask: Task<Void, Never>?

    func body(content view: Content) -> some View {
        view
            // Armed: the item lifts off the page — a touch smaller, a deeper
            // shadow, a thin accent edge — which is the cue that letting go
            // somewhere else will pop it out into a window.
            .overlay {
                RoundedRectangle(cornerRadius: Metrics.cardRadius, style: .continuous)
                    .strokeBorder(Palette.accent.opacity(armed ? 0.8 : 0), lineWidth: 1.5)
                    .allowsHitTesting(false)
            }
            .scaleEffect(armed ? 0.97 : 1)
            .shadow(color: .black.opacity(armed ? 0.18 : 0), radius: armed ? 14 : 0, y: armed ? 6 : 0)
            .animation(Motion.quick, value: armed)
            .contentShape(Rectangle())
            // `simultaneousGesture`, not `gesture`. The dock rows live inside a
            // ScrollView, and an exclusive gesture loses the arbitration to the
            // scroll view's own — so the press never arrived and tearing a
            // panel out stopped working entirely. Running alongside it is safe
            // now that this modifier owns the tap as well: there's no Button
            // underneath to also fire.
            .simultaneousGesture(
                DragGesture(minimumDistance: 0, coordinateSpace: .named(PanelHost.space))
                    .onChanged { value in
                        guard isEnabled else { return }
                        if pressTask == nil {
                            pressTask = Task {
                                try? await Task.sleep(for: .milliseconds(380))
                                guard !Task.isCancelled else { return }
                                await MainActor.run {
                                    armed = true
                                    // A small haptic-equivalent: the outline and
                                    // the shrink are the only signal that the
                                    // hold registered, so they matter.
                                }
                            }
                        }
                        // Moving before the hold completes means this was a
                        // drag of something else — a file, or a scroll — so the
                        // tear-off stands down and this stops counting as a
                        // click too.
                        if !armed, abs(value.translation.width)
                            + abs(value.translation.height) > 12 {
                            pressTask?.cancel()
                            pressTask = nil
                            cancelled = true
                        }
                    }
                    .onEnded { value in
                        pressTask?.cancel()
                        pressTask = nil
                        defer { cancelled = false }
                        if armed {
                            armed = false
                            // Tear-off only. The page behind stays exactly
                            // where it was.
                            onTearOff(content, value.location)
                        } else if !cancelled {
                            // A plain click.
                            onTap()
                        }
                    }
            )
    }
}

extension View {
    /// Handles both a click and a long-press-drag tear-off, so the two can
    /// never both fire from one press.
    func tearable(_ content: PanelContent, enabled: Bool = true,
                  onTap: @escaping () -> Void,
                  onTearOff: @escaping (PanelContent, CGPoint) -> Void) -> some View {
        modifier(TearOffModifier(content: content, isEnabled: enabled,
                                 onTap: onTap, onTearOff: onTearOff))
    }
}
