import Foundation

var pass = 0, fail = 0
func out(_ t: String) { print(t) }
func ok(_ n: String) { pass += 1; out("  ok   \(n)") }
func bad(_ n: String, _ d: String) { fail += 1; out("  FAIL \(n): \(d)") }

// MARK: - Contrast

func luminance(_ hex: String) -> Double {
    let (r, g, b) = PaletteTokens.rgb(from: hex)
    func lin(_ c: Double) -> Double {
        c <= 0.03928 ? c / 12.92 : pow((c + 0.055) / 1.055, 2.4)
    }
    return 0.2126 * lin(r) + 0.7152 * lin(g) + 0.0722 * lin(b)
}

func contrast(_ a: String, _ b: String) -> Double {
    let l1 = luminance(a), l2 = luminance(b)
    return (max(l1, l2) + 0.05) / (min(l1, l2) + 0.05)
}

out("── the reference palette is preserved exactly")

// The whole promise of the accent system is that picking "Yellow" leaves the
// original design untouched. Generated values would land within a point or two
// of these, which is invisible but still a change nobody asked for.
let yl = AccentTheme.yellow.lightTokens
let yd = AccentTheme.yellow.darkTokens
func same(_ n: String, _ got: String, _ want: String) {
    if got.uppercased() == want { ok("\(n) \(got)") } else { bad(n, "got \(got) want \(want)") }
}
same("light background", yl.background, "#F7F2E9")
same("light accent", yl.accent, "#D9B872")
same("light outline", yl.outline, "#2A2420")
same("light text", yl.text, "#241F19")
same("dark background", yd.background, "#1A1714")
same("dark accent", yd.accent, "#C9A45E")
same("dark text", yd.text, "#F0E7D8")

out("── every family is legible in both modes")

// WCAG AA is 4.5:1 for body text. Anything below that isn't a style choice,
// it's a bug — and a generated palette can drift there without anyone noticing.
for accent in AccentTheme.allCases {
    for (mode, tokens) in [("light", accent.lightTokens), ("dark", accent.darkTokens)] {
        let pairs: [(String, String, String)] = [
            ("text on background", tokens.text, tokens.background),
            ("text on surface", tokens.text, tokens.surface),
            ("secondary on background", tokens.textSecondary, tokens.background),
            ("onAccent on accent", tokens.onAccent, tokens.accent),
            ("text on well", tokens.text, tokens.well),
            // The accent used as a glyph/link colour — the original accent is
            // under 2:1 here, which is why `accentInk` exists.
            ("accentInk on surface", tokens.accentInk, tokens.surface),
            ("accentInk on selection", tokens.accentInk, tokens.selection),
            ("text on control", tokens.text, tokens.control),
            ("text on raised", tokens.text, tokens.raised),
        ]
        for (label, fg, bg) in pairs {
            let ratio = contrast(fg, bg)
            if ratio >= 4.5 {
                ok(String(format: "%@ %@ %@ %.1f:1", accent.label, mode, label, ratio))
            } else {
                bad("\(accent.label) \(mode) \(label)",
                    String(format: "%.2f:1 is below AA", ratio))
            }
        }
    }
}

out("── a selected segment reads as raised, not sunken")

// The dark surface is darker than the dark well, so a selection plate drawn in
// `surface` looked pressed in. `raised` must out-luminate `control` in both modes.
for accent in AccentTheme.allCases {
    for (mode, t) in [("light", accent.lightTokens), ("dark", accent.darkTokens)] {
        if luminance(t.raised) > luminance(t.control) {
            ok("\(accent.label) \(mode) raised above control")
        } else {
            bad("\(accent.label) \(mode) raised", "\(t.raised) not lighter than \(t.control)")
        }
    }
}

out("── families are actually distinct")

// A generator bug that ignored the hue would produce seven identical palettes
// and every contrast test above would still pass.
var seen: [String: String] = [:]
for accent in AccentTheme.allCases {
    let key = accent.lightTokens.accent
    if let other = seen[key] {
        bad("distinct accents", "\(accent.label) and \(other) are both \(key)")
    } else {
        seen[key] = accent.label
    }
}
if seen.count == AccentTheme.allCases.count {
    ok("all \(seen.count) families have a distinct accent colour")
}

out("── mono is neutral, the others are not")

func saturation(_ hex: String) -> Double {
    let (r, g, b) = PaletteTokens.rgb(from: hex)
    let mx = max(r, g, b), mn = min(r, g, b)
    return mx == 0 ? 0 : (mx - mn) / mx
}
let monoSat = saturation(AccentTheme.mono.lightTokens.accent)
if monoSat < 0.12 { ok(String(format: "mono accent is near-neutral (%.3f)", monoSat)) }
else { bad("mono", String(format: "saturation %.3f is too colourful", monoSat)) }

for accent in AccentTheme.allCases where accent != .mono {
    let s = saturation(accent.lightTokens.accent)
    if s > 0.2 { ok(String(format: "%@ accent carries colour (%.2f)", accent.label, s)) }
    else { bad(accent.label, String(format: "saturation %.2f is too flat", s)) }
}

out("── HSL conversion")

func expectHex(_ n: String, _ got: String, _ want: String) {
    if got == want { ok("\(n) -> \(got)") } else { bad(n, "got \(got) want \(want)") }
}
expectHex("pure white", PaletteTokens.hexFromHSL(hue: 0, saturation: 0, lightness: 1), "#FFFFFF")
expectHex("pure black", PaletteTokens.hexFromHSL(hue: 0, saturation: 0, lightness: 0), "#000000")
expectHex("mid grey", PaletteTokens.hexFromHSL(hue: 0, saturation: 0, lightness: 0.5), "#808080")
expectHex("pure red", PaletteTokens.hexFromHSL(hue: 0, saturation: 1, lightness: 0.5), "#FF0000")
expectHex("pure green", PaletteTokens.hexFromHSL(hue: 120, saturation: 1, lightness: 0.5), "#00FF00")
expectHex("pure blue", PaletteTokens.hexFromHSL(hue: 240, saturation: 1, lightness: 0.5), "#0000FF")
// Hue must wrap rather than clamp.
expectHex("hue wraps past 360",
          PaletteTokens.hexFromHSL(hue: 480, saturation: 1, lightness: 0.5), "#00FF00")
expectHex("negative hue wraps",
          PaletteTokens.hexFromHSL(hue: -120, saturation: 1, lightness: 0.5), "#0000FF")

out("── hex parsing round-trips")

for hex in ["#F7F2E9", "#000000", "#FFFFFF", "#D9B872", "#1A1714"] {
    let (r, g, b) = PaletteTokens.rgb(from: hex)
    let back = String(format: "#%02X%02X%02X",
                      Int((r * 255).rounded()), Int((g * 255).rounded()),
                      Int((b * 255).rounded()))
    if back == hex { ok("round-trip \(hex)") } else { bad("round-trip", "\(hex) -> \(back)") }
}

// Malformed input must degrade rather than crash.
let bogus = PaletteTokens.rgb(from: "not-a-colour")
if bogus == (0, 0, 0) { ok("malformed hex falls back to black") }
else { bad("malformed hex", "got \(bogus)") }

out("")
out("\(pass) passed, \(fail) failed")
exit(fail == 0 ? 0 : 1)
