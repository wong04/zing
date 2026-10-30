import Foundation
import CoreGraphics

// Floating-panel placement and the sidebar's recents list. Both are pure, so
// the rules are pinned here rather than eyeballed by dragging windows around.

var pass = 0, fail = 0
func out(_ t: String) { print(t) }
func check(_ name: String, _ condition: Bool, _ detail: @autoclosure () -> String = "") {
    if condition { pass += 1; out("  ok   \(name)") }
    else { fail += 1; out("  FAIL \(name) \(detail())") }
}

let window = CGRect(x: 0, y: 0, width: 1200, height: 800)
func r(_ x: CGFloat, _ y: CGFloat, _ w: CGFloat = 400, _ h: CGFloat = 300) -> CGRect {
    CGRect(x: x, y: y, width: w, height: h)
}
func noOverlaps(_ frames: [CGRect]) -> Bool {
    for i in frames.indices { for j in frames.indices where i < j {
        if frames[i].intersects(frames[j]) { return false }
    } }
    return true
}
let inner = window.insetBy(dx: PanelLayout.inset, dy: PanelLayout.inset)

out("── the dropped panel keeps its spot")
do {
    let frames = [r(100, 100), r(250, 150)]
    let out1 = PanelLayout.resolve(fixed: 1, frames: frames, in: window)
    check("dropped frame unchanged", out1[1] == frames[1], "\(out1[1])")
    check("the other one moved", out1[0] != frames[0])
    check("nothing overlaps afterwards", noOverlaps(out1))
    check("pushed panel is still inside the window", inner.contains(out1[0]), "\(out1[0])")
}

out("── panels that don't touch are left alone")
do {
    let frames = [r(20, 20), r(600, 400)]
    let result = PanelLayout.resolve(fixed: 0, frames: frames, in: window)
    check("untouched panels don't move", result == frames)
}

out("── the push is the shortest one available")
do {
    // B overlaps A's right edge by 40pt. The cheapest escape is right by
    // 40 + gap, not down by a whole panel height.
    let a = r(100, 100), b = r(460, 100)
    let result = PanelLayout.resolve(fixed: 0, frames: [a, b], in: window)
    check("pushed right, not down", result[1].minY == b.minY && result[1].minX > b.minX,
          "\(result[1])")
    check("cleared by exactly the gap", result[1].minX == a.maxX + PanelLayout.gap,
          "\(result[1].minX)")
}

out("── a panel pinned against the edge escapes the other way")
do {
    // B is at the right edge and A lands on it from the left: there's no room
    // to go right, so it has to go left, up or down.
    let b = r(790, 250), a = r(700, 250)
    let result = PanelLayout.resolve(fixed: 0, frames: [a, b], in: window)
    check("still inside the window", inner.contains(result[1]), "\(result[1])")
    check("no overlap", !result[0].intersects(result[1]))
}

out("── three panels in a pile all end up apart")
do {
    let frames = [r(300, 200), r(320, 220), r(340, 240)]
    let result = PanelLayout.resolve(fixed: 2, frames: frames, in: window)
    check("no overlaps between any pair", noOverlaps(result), "\(result)")
    check("all inside the window", result.allSatisfy { inner.contains($0) || frames.contains($0) })
}

out("── a window too small to separate them leaves them be")
do {
    let tiny = CGRect(x: 0, y: 0, width: 500, height: 350)
    let frames = [r(10, 10), r(60, 30)]
    let result = PanelLayout.resolve(fixed: 1, frames: frames, in: tiny)
    check("unplaceable panel isn't thrown off-screen", result[0] == frames[0], "\(result[0])")
}

out("── on a drop, the dropped panel may slide when that's the only way")
do {
    // The gallery case that exposed this: two big panels in a 1240pt window,
    // the dropped one in the middle, no room on either side of it.
    let big = CGRect(x: 0, y: 0, width: 1240, height: 800)
    let json = CGRect(x: 380, y: 102, width: 520, height: 580)
    let diag = CGRect(x: 460, y: 182, width: 480, height: 540)
    let held = PanelLayout.resolve(fixed: 1, frames: [json, diag], in: big)
    check("mid-drag the held panel never moves", held[1] == diag)
    let dropped = PanelLayout.resolve(fixed: 1, frames: [json, diag], in: big,
                                      allowMovingFixed: true)
    check("after the drop they no longer overlap", !dropped[0].intersects(dropped[1]),
          "\(dropped)")
    let area = big.insetBy(dx: PanelLayout.inset, dy: PanelLayout.inset)
    check("both are inside the window", area.contains(dropped[0]) && area.contains(dropped[1]))
}

do {
    // Speed: this runs on every drag frame, so three panels must resolve fast.
    let frames = [r(300, 200), r(320, 220), r(340, 240)]
    let start = Date()
    for _ in 0..<100 { _ = PanelLayout.resolve(fixed: 2, frames: frames, in: window) }
    let perCall = Date().timeIntervalSince(start) / 100
    check(String(format: "three panels resolve in %.2f ms (under 4)", perCall * 1000),
          perCall < 0.004)
}

out("── recents: most recent first, no duplicates, capped at three")
do {
    var list: [String] = []
    for id in ["a", "b", "c", "d"] { list = RecentList.touching(id, in: list) }
    check("capped and ordered", list == ["d", "c", "b"], "\(list)")
    list = RecentList.touching("b", in: list)
    check("reusing moves to the front without duplicating", list == ["b", "d", "c"], "\(list)")
    list = RecentList.removing("d", from: list)
    check("removing works", list == ["b", "c"], "\(list)")
}

out(""); out("\(pass) passed, \(fail) failed")
exit(fail == 0 ? 0 : 1)
