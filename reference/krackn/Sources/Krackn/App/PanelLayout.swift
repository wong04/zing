import CoreGraphics
import Foundation

/// Where floating panels go so they never sit on top of each other.
///
/// Pure geometry over rectangles, deliberately free of SwiftUI, so the rules
/// can be tested directly: the panel being placed always wins its spot, and
/// everything it lands on moves the *shortest* distance that clears it while
/// staying inside the window.
enum PanelLayout {
    /// Breathing room left between two panels after one is pushed.
    static let gap: CGFloat = 12
    /// Panels keep this far inside the window edge when they're pushed.
    static let inset: CGFloat = 8

    /// Resolves overlaps around `fixed`.
    ///
    /// - Parameters:
    ///   - fixed: index of the panel that was just dropped or opened. It keeps
    ///     exactly the frame it was given.
    ///   - frames: every panel's frame, in placement order.
    ///   - bounds: the area panels have to stay inside.
    /// - Returns: new frames, same order. A panel that can't be cleared without
    ///   leaving `bounds` — a window too small for everything — stays where it
    ///   was rather than being thrown off-screen.
    static func resolve(fixed: Int, frames: [CGRect], in bounds: CGRect,
                        allowMovingFixed: Bool = false) -> [CGRect] {
        guard frames.indices.contains(fixed) else { return frames }

        // Nearest first: the panel that was hit squarely gets first pick of
        // free space, and the search below meets good answers early.
        let others = frames.indices
            .filter { $0 != fixed }
            .sorted { distance(frames[$0], frames[fixed]) < distance(frames[$1], frames[fixed]) }

        // A joint search, not one panel at a time. Greedy placement fails in
        // exactly the case that matters: the first panel pushed takes the
        // middle of the only free column, and the second then has nowhere to
        // go even though the two of them fit stacked. Branch and bound over
        // each panel's few nearest free spots finds the layout that moves
        // things least in total. With the three-panel cap that's a few
        // thousand rectangle tests at most — cheap enough for every drag frame.
        var best: (cost: CGFloat, frames: [CGRect])?
        var current = frames
        func search(_ depth: Int, placed: [CGRect], cost: CGFloat) {
            if let best, cost >= best.cost { return }
            guard depth < others.count else {
                best = (cost, current)
                return
            }
            let index = others[depth]
            let home = frames[index]
            for spot in freeSpots(for: home, avoiding: placed, in: bounds).prefix(10) {
                current[index] = spot
                search(depth + 1, placed: placed + [spot],
                       cost: cost + hypot(spot.minX - home.minX, spot.minY - home.minY))
            }
            current[index] = home
        }
        search(0, placed: [frames[fixed]], cost: 0)
        if let best { return best.frames }

        // Nothing fits around the dropped panel where it is — two large
        // panels in a window that only has room for them side by side, say,
        // with the dropped one in the middle. On a drop (not mid-drag, where
        // the panel has to stay under the cursor) it may slide too, so that
        // both can fit. Its movement is weighted heavily: it should move as
        // little as possible, and only when that's the only way.
        if allowMovingFixed {
            let home = frames[fixed]
            for spot in freeSpots(for: home, avoiding: [], in: bounds) {
                let moved = hypot(spot.minX - home.minX, spot.minY - home.minY) * 3
                current = frames
                current[fixed] = spot
                search(0, placed: [spot], cost: moved)
            }
            if let best { return best.frames }
        }

        // No arrangement clears everything — the window is too small for the
        // panels. Do what can be done one at a time and leave the rest where
        // they were, rather than throwing anything off-screen.
        var result = frames
        var placed = [frames[fixed]]
        for index in others {
            if let spot = freeSpots(for: frames[index], avoiding: placed, in: bounds).first {
                result[index] = spot
            }
            placed.append(result[index])
        }
        return result
    }

    /// Every place `frame` could go without overlapping `occupied`, nearest
    /// first. Its current position counts if it's already clear — even when
    /// it hangs partly off the window, since the user may have put it there.
    static func freeSpots(for frame: CGRect, avoiding occupied: [CGRect],
                          in bounds: CGRect) -> [CGRect] {
        let area = bounds.insetBy(dx: inset, dy: inset)
        var xs: [CGFloat] = [frame.minX, area.minX, area.maxX - frame.width]
        var ys: [CGFloat] = [frame.minY, area.minY, area.maxY - frame.height]
        // Butting up against any occupied panel's edge, plus the window edges
        // so a panel in a corner can slide along the edge. Every combination
        // is tried, which finds diagonal escapes a one-axis push misses.
        for other in occupied {
            xs += [other.minX - frame.width - gap, other.maxX + gap]
            ys += [other.minY - frame.height - gap, other.maxY + gap]
        }
        var spots: [(CGRect, CGFloat)] = []
        for x in xs {
            for y in ys {
                let candidate = CGRect(x: x, y: y, width: frame.width, height: frame.height)
                guard !occupied.contains(where: { $0.intersects(candidate) }) else { continue }
                let isHome = candidate.origin == frame.origin
                guard isHome || area.contains(candidate) else { continue }
                spots.append((candidate, hypot(x - frame.minX, y - frame.minY)))
            }
        }
        var seen = Set<String>()
        return spots.sorted { $0.1 < $1.1 }
            .map(\.0)
            .filter { seen.insert("\($0.minX),\($0.minY)").inserted }
    }

    private static func distance(_ a: CGRect, _ b: CGRect) -> CGFloat {
        hypot(a.midX - b.midX, a.midY - b.midY)
    }
}

/// The sidebar's "recently used" list: most recent first, no duplicates,
/// capped. Stored as identifier strings so it survives relaunch without
/// recording anything more revealing than which tool was opened.
enum RecentList {
    static let capacity = 3

    static func touching(_ id: String, in list: [String],
                         capacity: Int = RecentList.capacity) -> [String] {
        var next = list.filter { $0 != id }
        next.insert(id, at: 0)
        return Array(next.prefix(capacity))
    }

    static func removing(_ id: String, from list: [String]) -> [String] {
        list.filter { $0 != id }
    }
}
