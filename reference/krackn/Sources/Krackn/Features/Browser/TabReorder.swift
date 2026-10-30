import SwiftUI

/// How wide each tab is. Safari's rule: tabs share the whole row equally —
/// one tab spans the window — and when there are too many for titles to be
/// readable, the one you're on keeps a readable width and the rest shrink to
/// their icons. Pinned tabs are always icons, at the left.
enum TabBarLayout {
    static let pinnedWidth: CGFloat = 34
    /// Room for the close button at the left *and* the icon beside it, so an
    /// icon-only tab never has to swap one for the other.
    static let iconWidth: CGFloat = 40
    /// Below this a title is mostly ellipsis; drop it and show the icon alone.
    static let minimumTitled: CGFloat = 110
    static let dividerWidth: CGFloat = 9

    struct Result: Equatable {
        var looseWidths: [CGFloat]
        /// Per loose tab: icon only.
        var compact: [Bool]
    }

    static func layout(total: CGFloat, pinned: Int, loose: Int, activeLoose: Int?,
                       spacing: CGFloat = 2) -> Result {
        guard loose > 0 else { return Result(looseWidths: [], compact: []) }
        var available = total - CGFloat(pinned) * (pinnedWidth + spacing)
        if pinned > 0 { available -= dividerWidth }
        let gaps = spacing * CGFloat(loose - 1)
        let equal = (available - gaps) / CGFloat(loose)
        if equal >= minimumTitled {
            return Result(looseWidths: Array(repeating: equal, count: loose),
                          compact: Array(repeating: false, count: loose))
        }
        guard let active = activeLoose, loose > 1 else {
            let w = max(equal, iconWidth)
            return Result(looseWidths: Array(repeating: w, count: loose),
                          compact: Array(repeating: true, count: loose))
        }
        var activeWidth = min(max(available * 0.25, 160), 240)
        var others = (available - activeWidth - gaps) / CGFloat(loose - 1)
        if others < iconWidth {
            // Icons are at their smallest: the active tab gives back width so
            // the row still fits, keeping its title while it can.
            others = iconWidth
            activeWidth = max(available - gaps - others * CGFloat(loose - 1), iconWidth)
        }
        var widths = Array(repeating: others, count: loose)
        widths[active] = activeWidth
        var compact = Array(repeating: others < minimumTitled, count: loose)
        compact[active] = activeWidth < minimumTitled
        return Result(looseWidths: widths, compact: compact)
    }
}

/// Geometry for dragging tabs into a new order.
///
/// This replaces `.onDrag`/`.onDrop`. System drag-and-drop is built for moving
/// data *between* views: on macOS it hijacks the click before a tap registers,
/// gives no continuous position, and only reports discrete enter/exit events —
/// so a tab jumps between slots instead of sliding under the cursor. A plain
/// drag gesture gives the position on every frame, which is what a browser's
/// tab strip actually needs.
enum TabReorder {

    /// Which slot a cursor at `x` belongs in, given each chip's frame in
    /// left-to-right order.
    ///
    /// A chip claims a slot once the cursor passes its midpoint, which is what
    /// makes neighbours swap cleanly instead of flickering while the pointer
    /// sits on a boundary.
    static func targetIndex(forX x: CGFloat, frames: [CGRect]) -> Int {
        guard !frames.isEmpty else { return 0 }

        for (index, frame) in frames.enumerated() {
            if x < frame.midX { return index }
        }
        return frames.count - 1
    }

    /// Which slot the dragged chip belongs in, judged by where the chip *is
    /// drawn* rather than where the cursor is.
    ///
    /// The old rule asked which chip's midpoint the cursor had passed, and the
    /// list included the dragged chip's own frame — so nudging a tab a few
    /// points right of its own centre already claimed the next slot, and the
    /// strip swapped on the slightest movement. Here a neighbour gives way
    /// only once the dragged chip's centre crosses *the neighbour's* centre.
    static func targetIndex(dragging index: Int, visualCenterX x: CGFloat,
                            frames: [CGRect]) -> Int {
        guard frames.indices.contains(index) else { return index }
        var target = index
        while target + 1 < frames.count, x > frames[target + 1].midX { target += 1 }
        while target - 1 >= 0, x < frames[target - 1].midX { target -= 1 }
        return target
    }

    /// Where the dragged chip's centre will sit once it has moved from
    /// `from` to `to`, computed from the frames *before* the move.
    ///
    /// Needed because the chips' real frames only arrive after the next
    /// layout pass. Re-anchoring on those stale frames straight after a swap
    /// put the chip a whole slot away from the cursor, so it lurched and then
    /// fought the pointer — the "tabs won't drag" symptom.
    static func centerAfterMove(from: Int, to: Int, frames: [CGRect]) -> CGFloat {
        guard frames.indices.contains(from), frames.indices.contains(to) else {
            return frames.indices.contains(from) ? frames[from].midX : 0
        }
        let width = frames[from].width
        if to > from { return frames[to].maxX - width / 2 }
        if to < from { return frames[to].minX + width / 2 }
        return frames[from].midX
    }

    /// Reports each chip's frame up to the strip so the drag can do geometry.
    struct FramePreference: PreferenceKey {
        static var defaultValue: [UUID: CGRect] { [:] }
        static func reduce(value: inout [UUID: CGRect], nextValue: () -> [UUID: CGRect]) {
            value.merge(nextValue()) { _, new in new }
        }
    }

    /// Coordinate space the strip publishes frames in.
    static let space = "aio.tabstrip"
}

extension View {
    /// Publishes this chip's frame for the reorder geometry.
    func reportsTabFrame(id: UUID) -> some View {
        background(
            GeometryReader { geo in
                Color.clear.preference(
                    key: TabReorder.FramePreference.self,
                    value: [id: geo.frame(in: .named(TabReorder.space))])
            }
        )
    }
}

extension Array {
    /// Nil instead of a crash when a layout and the tab list disagree for a
    /// frame — a tab closing mid-animation, say.
    subscript(safe index: Int) -> Element? {
        indices.contains(index) ? self[index] : nil
    }
}
