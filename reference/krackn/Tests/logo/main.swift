import SwiftUI

// Stub so KracknLogo.swift's default args typecheck without the theme layer.
// The parser and art under test reference no Palette tokens themselves.
enum Palette {
    static var accent: Color { .clear }
    static var onAccent: Color { .clear }
}

var pass = 0, fail = 0
func check(_ n: String, _ c: Bool, _ d: String = "") {
    if c { pass += 1 } else { fail += 1; print("  FAIL \(n) \(d)") }
}

// --- the real logo art parses and lands where it should ---

let vb = KracknLogoArt.viewBox
check("viewBox is the SVG's", vb == CGSize(width: 236, height: 231))
check("three body paths", KracknLogoArt.body.count == 3, "\(KracknLogoArt.body.count)")
check("two eye paths", KracknLogoArt.eyes.count == 2, "\(KracknLogoArt.eyes.count)")

// Body: parsed in native viewBox coords, must be non-empty and roughly fill it.
var bodyBounds = CGRect.null
for d in KracknLogoArt.body { bodyBounds = bodyBounds.union(SVGPathParser.path(from: d).boundingRect) }
check("body non-empty", !bodyBounds.isNull && bodyBounds.width > 0)
check("body spans most of the width", bodyBounds.width > vb.width * 0.9,
      "w=\(Int(bodyBounds.width))")
check("body spans most of the height", bodyBounds.height > vb.height * 0.9,
      "h=\(Int(bodyBounds.height))")
check("body stays within the viewBox", bodyBounds.minX > -2 && bodyBounds.minY > -2
      && bodyBounds.maxX < vb.width + 2 && bodyBounds.maxY < vb.height + 2,
      "\(bodyBounds)")

// Eyes: small, and sitting in the upper-middle where a face's eyes go.
var eyeBounds = CGRect.null
for d in KracknLogoArt.eyes { eyeBounds = eyeBounds.union(SVGPathParser.path(from: d).boundingRect) }
check("eyes non-empty", !eyeBounds.isNull && eyeBounds.width > 0)
check("eyes are small", eyeBounds.width < vb.width * 0.6 && eyeBounds.height < vb.height * 0.3,
      "\(eyeBounds.size)")
check("eyes sit in the upper half", eyeBounds.midY < vb.height * 0.5,
      "midY=\(Int(eyeBounds.midY))")
check("eyes sit inside the body", bodyBounds.contains(eyeBounds), "\(eyeBounds)")

// SVGShape aspect-fits and centres into an arbitrary rect without escaping it.
let fitted = SVGShape(paths: KracknLogoArt.body).path(in: CGRect(x: 0, y: 0, width: 100, height: 100))
let fb = fitted.boundingRect
check("fitted stays in the frame",
      fb.minX >= -0.5 && fb.minY >= -0.5 && fb.maxX <= 100.5 && fb.maxY <= 100.5, "\(fb)")

// --- parser robustness ---

check("empty string -> empty path", SVGPathParser.path(from: "").isEmpty)
check("whitespace -> empty path", SVGPathParser.path(from: "   \n  ").isEmpty)

// Absolute triangle.
let tri = SVGPathParser.path(from: "M0 0 L10 0 L10 10 Z").boundingRect
check("absolute M/L/Z", tri == CGRect(x: 0, y: 0, width: 10, height: 10), "\(tri)")

// Relative commands and run-on numbers ("1.5.5" = 1.5 then .5).
let rel = SVGPathParser.path(from: "m0 0l10 0l0 10z").boundingRect
check("relative m/l/z", rel == CGRect(x: 0, y: 0, width: 10, height: 10), "\(rel)")
let runon = SVGPathParser.path(from: "M1.5.5L2 2")
check("run-on decimals don't crash", !runon.isEmpty)

// Implicit repeat: several coordinate pairs after one L.
let repeated = SVGPathParser.path(from: "M0 0 L5 0 5 5 0 5 Z").boundingRect
check("implicit repeated L", repeated == CGRect(x: 0, y: 0, width: 5, height: 5), "\(repeated)")

// An unsupported command must stop, not loop forever.
let unsupported = SVGPathParser.path(from: "M0 0 L10 10 A5 5 0 0 1 20 20")
check("unsupported command stops cleanly", !unsupported.isEmpty)

print("\(pass) passed, \(fail) failed")
exit(fail == 0 ? 0 : 1)
