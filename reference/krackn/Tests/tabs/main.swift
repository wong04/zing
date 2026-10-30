import SwiftUI
import WebKit
func out(_ t: String) { print(t) }
var pass = 0, fail = 0
func check(_ n: String, _ got: Int, _ want: Int) {
    if got == want { pass += 1; out("  ok   \(n) -> \(got)") }
    else { fail += 1; out("  FAIL \(n): got \(got) want \(want)") }
}

// Four chips of differing widths, laid out left to right.
// widths 80, 120, 60, 100 with 4pt gaps starting at x=8
var frames: [CGRect] = []
var x: CGFloat = 8
for w in [80.0, 120.0, 60.0, 100.0] {
    frames.append(CGRect(x: x, y: 0, width: w, height: 28)); x += w + 4
}
out("frames: " + frames.map { "[\(Int($0.minX))-\(Int($0.maxX)) mid \(Int($0.midX))]" }.joined(separator: " "))

// Cursor left of everything -> slot 0
check("far left", TabReorder.targetIndex(forX: 0, frames: frames), 0)
// Inside chip 0, before its midpoint -> 0
check("left half of chip 0", TabReorder.targetIndex(forX: 20, frames: frames), 0)
// Past chip 0's midpoint -> claims slot 1
check("past chip 0 mid", TabReorder.targetIndex(forX: 60, frames: frames), 1)
// Past chip 1's midpoint
check("past chip 1 mid", TabReorder.targetIndex(forX: 170, frames: frames), 2)
// Past chip 2's midpoint
check("past chip 2 mid", TabReorder.targetIndex(forX: 250, frames: frames), 3)
// Far right -> last slot
check("far right", TabReorder.targetIndex(forX: 9999, frames: frames), 3)
// Degenerate input must not crash
check("empty frames", TabReorder.targetIndex(forX: 50, frames: []), 0)

// Boundary: exactly at a midpoint should be deterministic, not flickery
let m0 = frames[0].midX
let a = TabReorder.targetIndex(forX: m0 - 0.5, frames: frames)
let b = TabReorder.targetIndex(forX: m0 + 0.5, frames: frames)
if a == 0 && b == 1 { pass += 1; out("  ok   midpoint boundary is stable (\(a) -> \(b))") }
else { fail += 1; out("  FAIL midpoint boundary: \(a) -> \(b)") }

// Monotonic: sweeping right must never move the target backwards.
var last = -1, monotonic = true
for px in stride(from: CGFloat(0), through: 400, by: 2) {
    let t = TabReorder.targetIndex(forX: px, frames: frames)
    if t < last { monotonic = false; break }
    last = t
}
if monotonic { pass += 1; out("  ok   sweep is monotonic (no flicker)") }
else { fail += 1; out("  FAIL sweep jumps backwards") }

// ---- simulate a full drag: pick up chip 0, sweep right to the end ----
// Mirrors what the gesture does each frame: find the target slot, move, repeat.
func simulate(order: [String], widths: [CGFloat], drag index: Int, toX: CGFloat) -> [String] {
    var tabs = order
    var current = index
    func layout() -> [CGRect] {
        var out: [CGRect] = []; var x: CGFloat = 8
        for t in tabs {
            let w = widths[order.firstIndex(of: t)!]
            out.append(CGRect(x: x, y: 0, width: w, height: 28)); x += w + 4
        }
        return out
    }
    // Step the cursor across in small increments, like real drag frames.
    var px: CGFloat = layout()[current].midX
    while px < toX {
        px += 3
        let target = TabReorder.targetIndex(forX: px, frames: layout())
        if target != current {
            let item = tabs.remove(at: current)
            tabs.insert(item, at: target)
            current = target
        }
    }
    return tabs
}

let names = ["A", "B", "C", "D"]
let widths: [CGFloat] = [80, 120, 60, 100]

let r1 = simulate(order: names, widths: widths, drag: 0, toX: 400)
if r1 == ["B","C","D","A"] { pass += 1; out("  ok   drag A fully right -> \(r1.joined())") }
else { fail += 1; out("  FAIL drag A right: got \(r1.joined()) want BCDA") }

let r2 = simulate(order: names, widths: widths, drag: 0, toX: 150)
if r2.count == 4 && Set(r2) == Set(names) { pass += 1; out("  ok   partial drag keeps all tabs -> \(r2.joined())") }
else { fail += 1; out("  FAIL partial drag lost tabs: \(r2.joined())") }

let r3 = simulate(order: names, widths: widths, drag: 2, toX: 60)
if Set(r3) == Set(names) { pass += 1; out("  ok   drag C left keeps all tabs -> \(r3.joined())") }
else { fail += 1; out("  FAIL drag left lost tabs: \(r3.joined())") }

// MARK: - Dragging by the chip's drawn position

out("── a dragged tab swaps only once it crosses its neighbour's centre")
// Chip 1 (120 wide, mid 152) dragged right. Chip 2 spans 216–276, mid 246.
check("nudged 10pt right: stays", TabReorder.targetIndex(dragging: 1, visualCenterX: 162, frames: frames), 1)
check("just short of neighbour's centre: stays", TabReorder.targetIndex(dragging: 1, visualCenterX: 245, frames: frames), 1)
check("past neighbour's centre: swaps", TabReorder.targetIndex(dragging: 1, visualCenterX: 247, frames: frames), 2)
check("far right: last slot", TabReorder.targetIndex(dragging: 1, visualCenterX: 9999, frames: frames), 3)
check("dragged left past chip 0's centre", TabReorder.targetIndex(dragging: 1, visualCenterX: 47, frames: frames), 0)
check("dragged left, not far enough", TabReorder.targetIndex(dragging: 1, visualCenterX: 49, frames: frames), 1)

out("── the chip's centre after a move is predicted, not read from stale frames")
// Moving chip 1 (x 92–212) into slot 2: it ends where chip 2 ended (276).
let after12 = TabReorder.centerAfterMove(from: 1, to: 2, frames: frames)
check("moved right: lands flush with the old right edge", Int(after12), Int(frames[2].maxX - 60))
let after10 = TabReorder.centerAfterMove(from: 1, to: 0, frames: frames)
check("moved left: lands flush with the old left edge", Int(after10), Int(frames[0].minX + 60))
// And the prediction matches a real reflow of the new order.
var reflowX: CGFloat = 8
var reflowMid: CGFloat = 0
for (i, w) in [80.0, 60.0, 120.0, 100.0].enumerated() {
    if i == 2 { reflowMid = reflowX + w / 2 }
    reflowX += w + 4
}
check("prediction matches the reflowed layout", Int(after12), Int(reflowMid))

// MARK: - Tab widths

func near(_ a: CGFloat, _ b: CGFloat) -> Bool { abs(a - b) < 0.5 }
func expectTrue(_ n: String, _ c: Bool) {
    if c { pass += 1; out("  ok   \(n)") } else { fail += 1; out("  FAIL \(n)") }
}
out("── tabs share the full width, compressing to icons when crowded")
let one = TabBarLayout.layout(total: 1000, pinned: 0, loose: 1, activeLoose: 0)
expectTrue("one tab spans the row", near(one.looseWidths[0], 1000) && one.compact == [false])
let four = TabBarLayout.layout(total: 1000, pinned: 0, loose: 4, activeLoose: 2)
expectTrue("four tabs split it equally", four.looseWidths.allSatisfy { near($0, (1000 - 6) / 4) })
expectTrue("and keep their titles", four.compact.allSatisfy { !$0 })
let pinned = TabBarLayout.layout(total: 1000, pinned: 3, loose: 2, activeLoose: 0)
let room = 1000 - 3 * (TabBarLayout.pinnedWidth + 2) - TabBarLayout.dividerWidth
expectTrue("pinned icons come off the top first", near(pinned.looseWidths.reduce(0, +) + 2, room))
let crowd = TabBarLayout.layout(total: 1000, pinned: 0, loose: 20, activeLoose: 5)
expectTrue("crowded: the active tab keeps a title", !crowd.compact[5] && crowd.looseWidths[5] >= TabBarLayout.minimumTitled)
expectTrue("crowded: the rest are icons", crowd.compact.enumerated().allSatisfy { $0.offset == 5 || $0.element })
expectTrue("crowded: everything still fits the row",
           crowd.looseWidths.reduce(0, +) + 19 * 2 <= 1000.5)
let huge = TabBarLayout.layout(total: 600, pinned: 0, loose: 60, activeLoose: 0)
expectTrue("never narrower than an icon", huge.looseWidths.allSatisfy { $0 >= TabBarLayout.iconWidth })
let pinnedActive = TabBarLayout.layout(total: 500, pinned: 1, loose: 10, activeLoose: nil)
expectTrue("active tab pinned: loose tabs share evenly as icons",
           pinnedActive.compact.allSatisfy { $0 } && Set(pinnedActive.looseWidths.map { Int($0) }).count == 1)

let icons = TabBarLayout.layout(total: 1000, pinned: 0, loose: 20, activeLoose: 0)
expectTrue("icon tabs leave room for the close button beside the icon",
           icons.looseWidths.enumerated().allSatisfy { $0.offset == 0 || $0.element >= 40 })

out("── the tab dropdown gets clicks over the page")
// A web view is an AppKit subview and wins hit-testing over SwiftUI drawn
// above it: clicks and right-clicks on the dropdown's rows went to the page.
let shieldBox = WebViewHost.Container(frame: NSRect(x: 0, y: 0, width: 200, height: 200))
let page = NSView(frame: shieldBox.bounds)
page.registerForDraggedTypes([.fileURL, .URL])
shieldBox.addSubview(page)
expectTrue("unshielded: the page takes the click", shieldBox.hitTest(NSPoint(x: 50, y: 50)) != nil)
shieldBox.shielded = true
expectTrue("shielded: the click passes over the page", shieldBox.hitTest(NSPoint(x: 50, y: 50)) == nil)
expectTrue("shielded: the page takes no drags", page.registeredDraggedTypes.isEmpty)
shieldBox.shielded = false
expectTrue("unshielded again: its drag types come back",
           Set(page.registeredDraggedTypes) == [.fileURL, .URL])

out("── a rebuilt view tree keeps the page on screen")
// An accent change rebuilds the views: the new container takes the web view,
// then the old one claims it back as it's torn down. Measured: the page ended
// up in the detached container — blank — until handed off on release.
let hostWindow = NSWindow(contentRect: NSRect(x: -5000, y: -5000, width: 300, height: 300),
                          styleMask: [.borderless], backing: .buffered, defer: false)
let pageView = WKWebView(frame: .zero)
let oldBox = WebViewHost.Container(frame: hostWindow.contentView!.bounds)
let newBox = WebViewHost.Container(frame: hostWindow.contentView!.bounds)
hostWindow.contentView!.addSubview(oldBox)
oldBox.wanted = pageView
hostWindow.contentView!.addSubview(newBox)
newBox.wanted = pageView
oldBox.adopt()   // the stale container claims it last
expectTrue("stale claim: the old container holds it", pageView.superview === oldBox)
oldBox.removeFromSuperview()
RunLoop.main.run(until: Date().addingTimeInterval(0.1))
expectTrue("old container gone: the page moves to the new one", pageView.superview === newBox)
expectTrue("and is still in the window", pageView.window === hostWindow)

out("── a fullscreen video isn't pulled back into the window")
// WebKit moves the web view into its own fullscreen window; reclaiming it on
// the next redraw would end fullscreen mid-video.
final class FakeFullscreenWebView: WKWebView {
    var fakeState: WKWebView.FullscreenState = .notInFullscreen {
        willSet { willChangeValue(forKey: "fullscreenState") }
        didSet { didChangeValue(forKey: "fullscreenState") }
    }
    override var fullscreenState: WKWebView.FullscreenState { fakeState }
}
let videoWindow = NSWindow(contentRect: NSRect(x: -5000, y: -5000, width: 300, height: 300),
                           styleMask: [.borderless], backing: .buffered, defer: false)
let video = FakeFullscreenWebView(frame: .zero)
let videoBox = WebViewHost.Container(frame: videoWindow.contentView!.bounds)
videoWindow.contentView!.addSubview(videoBox)
videoBox.wanted = video
expectTrue("starts in its container", video.superview === videoBox)
// What WebKit does on entering fullscreen.
video.fakeState = .inFullscreen
let fullscreenHost = NSView()
fullscreenHost.addSubview(video)
videoBox.adopt()   // a redraw while the video plays
expectTrue("a redraw leaves the fullscreen video alone", video.superview === fullscreenHost)
// Leaving fullscreen: the container takes it back.
video.fakeState = .notInFullscreen
RunLoop.main.run(until: Date().addingTimeInterval(0.1))
expectTrue("after fullscreen it's back in the window", video.superview === videoBox && video.window === videoWindow)

out(""); out("\(pass) passed, \(fail) failed")
exit(fail == 0 ? 0 : 1)
