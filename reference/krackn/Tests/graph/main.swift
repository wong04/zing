import Foundation
import CoreGraphics
var pass = 0, fail = 0
func check(_ n: String, _ c: Bool, _ d: String = "") {
    if c { pass += 1 } else { fail += 1; print("  FAIL \(n) \(d)") }
}
func near(_ a: Double, _ b: Double, _ tol: Double = 1e-6) -> Bool { abs(a-b) <= tol }

print("variables in expressions")
check("x binds", try! Expression.evaluate("x^2", variables: ["x": 3]) == 9)
check("x in functions", near(try! Expression.evaluate("sin(x)", variables: ["x": 0]), 0))
check("implicit mult with x", try! Expression.evaluate("2x", variables: ["x": 5]) == 10)
check("var beats constant e", try! Expression.evaluate("e", variables: ["e": 42]) == 42)
check("pi still works alongside x", near(try! Expression.evaluate("x*pi", variables: ["x": 2]), 2 * .pi))
do { _ = try Expression.evaluate("x+1"); fail += 1; print("  FAIL unbound x should throw") }
catch { pass += 1 }

print("makeFunction")
let f = Expression.makeFunction("x^2+1")
check("f(0)", try! f(0) == 1); check("f(3)", try! f(3) == 10)

print("viewport mapping")
let vp = GraphEngine.Viewport.default
let size = CGSize(width: 800, height: 520)
check("xMin maps to 0", near(GraphEngine.screenX(vp.xMin, viewport: vp, size: size), 0))
check("xMax maps to width", near(GraphEngine.screenX(vp.xMax, viewport: vp, size: size), 800))
check("yMax maps to top (0)", near(GraphEngine.screenY(vp.yMax, viewport: vp, size: size), 0))
check("yMin maps to bottom", near(GraphEngine.screenY(vp.yMin, viewport: vp, size: size), 520))
check("round-trip x", near(GraphEngine.graphX(GraphEngine.screenX(3.5, viewport: vp, size: size), viewport: vp, size: size), 3.5))
check("round-trip y", near(GraphEngine.graphY(GraphEngine.screenY(-2.25, viewport: vp, size: size), viewport: vp, size: size), -2.25))

print("zoom anchoring")
let anchor = (x: 4.0, y: 2.0)
let zoomed = vp.zoomed(by: 0.5, around: anchor)
check("zoom halves width", near(zoomed.width, vp.width * 0.5))
// The anchor must stay at the same screen position after zooming.
let beforePx = GraphEngine.screenX(anchor.x, viewport: vp, size: size)
let afterPx = GraphEngine.screenX(anchor.x, viewport: zoomed, size: size)
check("anchor stays put under cursor", near(beforePx, afterPx, 1e-6), "\(beforePx) vs \(afterPx)")
check("zoom clamps absurd factors", vp.zoomed(by: 1e30, around: anchor).width <= 1e9)
check("zoom clamps to non-zero", vp.zoomed(by: 1e-30, around: anchor).width >= 1e-9)
check("pan shifts both bounds", vp.panned(dx: 2, dy: -1) == GraphEngine.Viewport(xMin: -8, xMax: 12, yMin: -7.5, yMax: 5.5))

print("gridlines")
check("step for 20 range", GraphEngine.tickStep(range: 20) == 2, "\(GraphEngine.tickStep(range: 20))")
check("step for 1 range", GraphEngine.tickStep(range: 1) == 0.1, "\(GraphEngine.tickStep(range: 1))")
check("step for 0.05 range", near(GraphEngine.tickStep(range: 0.05), 0.005), "\(GraphEngine.tickStep(range: 0.05))")
check("step for huge range", GraphEngine.tickStep(range: 1e6) > 0)
check("degenerate range is safe", GraphEngine.tickStep(range: 0) == 1)
let t = GraphEngine.ticks(min: -10, max: 10, step: 5)
check("ticks span range", t == [-10, -5, 0, 5, 10], "\(t)")
check("ticks bail on absurd counts", GraphEngine.ticks(min: 0, max: 1e9, step: 1e-6).isEmpty)
check("label drops fp noise", GraphEngine.axisLabel(0.30000000000000004, step: 0.1) == "0.3",
      GraphEngine.axisLabel(0.30000000000000004, step: 0.1))
check("zero label", GraphEngine.axisLabel(0, step: 1) == "0")

print("sampling")
let line = GraphEngine.sample({ $0 }, viewport: vp, size: size)
check("line is one segment", line.count == 1, "\(line.count)")
check("line sampled densely", (line.first?.count ?? 0) > 500)
// 1/x must break at the asymptote, not draw through it.
let hyperbola = GraphEngine.sample({ 1 / $0 }, viewport: vp, size: size)
check("1/x splits at asymptote", hyperbola.count >= 2, "\(hyperbola.count) segments")
// tan has many asymptotes in [-10,10]
let tangent = GraphEngine.sample({ tan($0) }, viewport: vp, size: size)
check("tan splits repeatedly", tangent.count >= 4, "\(tangent.count) segments")
// sqrt is undefined for x<0 — no segment should start left of 0
let root = GraphEngine.sample({ $0 < 0 ? Double.nan : sqrt($0) }, viewport: vp, size: size)
check("sqrt only defined half", root.count == 1, "\(root.count)")
let firstX = root.first?.first?.x ?? 0
check("sqrt starts at origin", firstX >= size.width/2 - 2, "\(firstX)")
// A function that throws everywhere yields nothing, and doesn't crash.
struct E: Error {}
check("all-undefined is empty", GraphEngine.sample({ _ in throw E() }, viewport: vp, size: size).isEmpty)
check("zero-size is safe", GraphEngine.sample({ $0 }, viewport: vp, size: .zero).isEmpty)

print("roots")
let r = GraphEngine.roots({ $0 * $0 - 4 }, viewport: vp)
check("x^2-4 has two roots", r.count == 2, "\(r)")
check("roots near ±2", r.allSatisfy { near(abs($0), 2, 1e-6) }, "\(r)")

// Poles are not roots: tan(x) is zero at multiples of pi, and undefined
// (not zero) at odd multiples of pi/2.
let tanRoots = GraphEngine.roots({ tan($0) }, viewport: vp)
let poles = tanRoots.filter { r in
    let nearestHalfPi = (r / (.pi/2)).rounded()
    return Int(nearestHalfPi) % 2 != 0 && abs(r - nearestHalfPi * .pi/2) < 1e-3
}
check("tan poles not reported as roots", poles.isEmpty, "\(poles)")
check("tan roots are multiples of pi",
      tanRoots.allSatisfy { abs($0 - ($0 / .pi).rounded() * .pi) < 1e-4 }, "\(tanRoots)")
check("tan finds the expected zeros", tanRoots.count >= 6, "\(tanRoots.count)")
// 1/x has no roots at all, despite the sign flip at zero.
let hypRoots = GraphEngine.roots({ 1 / $0 }, viewport: vp)
check("1/x has no roots", hypRoots.isEmpty, "\(hypRoots)")


print("intersections")
// sin(x) and x/3 meet at 0 and two symmetric points.
let sinF: (Double) throws -> Double = { sin($0) }
let lineF: (Double) throws -> Double = { $0 / 3 }
let ix = GraphEngine.intersections(sinF, lineF, viewport: vp)
check("sin/line intersections found", ix.count >= 3, "\(ix.count)")
check("origin is an intersection", ix.contains { abs($0.x) < 1e-6 }, "\(ix.map { $0.x })")
check("intersections satisfy both curves",
      ix.allSatisfy { abs(sin($0.x) - $0.x/3) < 1e-6 }, "\(ix.map { $0.x })")
check("intersection y matches f(x)",
      ix.allSatisfy { abs($0.y - sin($0.x)) < 1e-6 })

// Parallel lines never meet.
let par = GraphEngine.intersections({ $0 + 1 }, { $0 - 1 }, viewport: vp)
check("parallel lines have none", par.isEmpty, "\(par.count)")

// x^2 and 4 meet at exactly ±2.
let quad = GraphEngine.intersections({ $0 * $0 }, { _ in 4 }, viewport: vp)
check("x^2 = 4 has two solutions", quad.count == 2, "\(quad.count)")
check("at x = ±2", quad.allSatisfy { abs(abs($0.x) - 2) < 1e-6 }, "\(quad.map { $0.x })")

// A pole must not be reported where 1/x crosses a line.
let poleCase = GraphEngine.intersections({ 1 / $0 }, { _ in 0 }, viewport: vp)
check("1/x never equals 0", poleCase.isEmpty, "\(poleCase.count)")

print("extrema")
// sin over [-10,10] turns at ±pi/2, ±3pi/2, ...
let turns = GraphEngine.extrema(sinF, viewport: vp)
check("sin has several turning points", turns.count >= 5, "\(turns.count)")
check("turning points sit near ±1",
      turns.allSatisfy { abs(abs($0.y) - 1) < 0.05 }, "\(turns.map { $0.y })")
check("both maxima and minima found",
      turns.contains { $0.isMaximum } && turns.contains { !$0.isMaximum })
// A straight line has none.
check("line has no turning points", GraphEngine.extrema({ 2 * $0 }, viewport: vp).isEmpty)

print("\n\(pass) passed, \(fail) failed")
exit(fail == 0 ? 0 : 1)
