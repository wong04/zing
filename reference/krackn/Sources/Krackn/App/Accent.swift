import Foundation

/// The colour families the whole app can be tinted with.
///
/// The original beige is kept byte-for-byte as `.yellow` — it's the reference
/// the others are derived from, and re-deriving it through the generator would
/// shift a few channels by a point for no reason. Every other family is built
/// by rotating that palette's hue while holding its lightness profile fixed, so
/// a blue Krackn has exactly the same contrast relationships and "tone and
/// shade" as the beige one rather than being a separate hand-picked palette
/// that drifts.
enum AccentTheme: String, CaseIterable, Identifiable {
    case yellow, green, blue, magenta, purple, red, mono

    var id: String { rawValue }

    var label: String {
        switch self {
        case .yellow: return "Yellow"
        case .green: return "Green"
        case .blue: return "Blue"
        case .magenta: return "Magenta"
        case .purple: return "Purple"
        case .red: return "Red"
        case .mono: return "Mono"
        }
    }

    /// A representative swatch for the settings picker — the accent itself.
    var swatchHex: String { lightTokens.accent }

    /// Base hue in degrees, and how much of the reference palette's saturation
    /// to keep. Warmer hues carry saturation well; the cooler and more magenta
    /// ones read as garish at full strength, so they're pulled back slightly.
    /// `mono` keeps a trace of warmth rather than going dead neutral, which
    /// would clash with the near-black outline the design depends on.
    private var profile: (hue: Double, saturation: Double) {
        switch self {
        case .yellow: return (38, 1.00)
        case .green: return (142, 0.82)
        case .blue: return (211, 0.88)
        case .magenta: return (322, 0.80)
        case .purple: return (272, 0.82)
        case .red: return (6, 0.84)
        case .mono: return (34, 0.07)
        }
    }

    // MARK: - Current selection

    static var current: AccentTheme {
        AccentTheme(rawValue: UserDefaults.standard.string(forKey: AppSettings.Key.accent) ?? "")
            ?? .yellow
    }

    // MARK: - Token generation

    var lightTokens: PaletteTokens {
        if self == .yellow { return .referenceLight }
        return PaletteTokens.generate(from: PaletteTokens.lightProfile,
                                      hue: profile.hue, saturation: profile.saturation)
    }

    var darkTokens: PaletteTokens {
        if self == .yellow { return .referenceDark }
        return PaletteTokens.generate(from: PaletteTokens.darkProfile,
                                      hue: profile.hue, saturation: profile.saturation)
    }
}

/// The palette as CSS-ready hex, so web content the app renders (the browser's
/// start page) is coloured from exactly the same numbers as the native UI — no
/// hand-copied values to drift apart.
struct PaletteTokens: Equatable {
    let background, surface, tint, well, border, outline: String
    let accent, onAccent, selection: String
    let text, textSecondary: String

    // MARK: - The reference palette

    /// The original beige, unchanged. Everything else is measured against this.
    static let referenceLight = PaletteTokens(
        background: "#F7F2E9", surface: "#FFFDF8", tint: "#EFE6D6",
        well: "#EBE1CF", border: "#DECFB6", outline: "#2A2420",
        accent: "#D9B872", onAccent: "#2A2118", selection: "#E6D3AC",
        text: "#241F19", textSecondary: "#6E6355")

    static let referenceDark = PaletteTokens(
        background: "#1A1714", surface: "#241F1A", tint: "#201B16",
        well: "#2C251E", border: "#3A322A", outline: "#CDBFA6",
        accent: "#C9A45E", onAccent: "#1A1410", selection: "#3D3325",
        text: "#F0E7D8", textSecondary: "#A69884")

    // MARK: - Profiles

    /// Saturation and lightness for each role, measured off the reference
    /// palette above. Rotating the hue through these keeps every contrast pair
    /// — text on background, onAccent on accent — at the ratio the beige theme
    /// was designed around.
    struct Role {
        let saturation: Double
        let lightness: Double
    }

    struct Profile {
        let background, surface, tint, well, border, outline: Role
        let accent, onAccent, selection: Role
        let text, textSecondary: Role
        /// Roles that must stay near-neutral ink rather than take the hue at
        /// full strength, or dark mode's "outline" turns into a coloured haze.
        let inkDamping: Double
    }

    static let lightProfile = Profile(
        background: Role(saturation: 0.47, lightness: 0.941),
        surface: Role(saturation: 1.00, lightness: 0.986),
        tint: Role(saturation: 0.44, lightness: 0.888),
        well: Role(saturation: 0.41, lightness: 0.867),
        border: Role(saturation: 0.38, lightness: 0.792),
        outline: Role(saturation: 0.135, lightness: 0.145),
        accent: Role(saturation: 0.575, lightness: 0.649),
        onAccent: Role(saturation: 0.27, lightness: 0.130),
        selection: Role(saturation: 0.54, lightness: 0.790),
        text: Role(saturation: 0.18, lightness: 0.120),
        textSecondary: Role(saturation: 0.128, lightness: 0.382),
        inkDamping: 0.55)

    static let darkProfile = Profile(
        background: Role(saturation: 0.13, lightness: 0.090),
        surface: Role(saturation: 0.16, lightness: 0.122),
        tint: Role(saturation: 0.185, lightness: 0.106),
        well: Role(saturation: 0.19, lightness: 0.145),
        border: Role(saturation: 0.16, lightness: 0.196),
        outline: Role(saturation: 0.28, lightness: 0.727),
        accent: Role(saturation: 0.498, lightness: 0.578),
        onAccent: Role(saturation: 0.236, lightness: 0.082),
        selection: Role(saturation: 0.245, lightness: 0.192),
        text: Role(saturation: 0.44, lightness: 0.894),
        textSecondary: Role(saturation: 0.16, lightness: 0.584),
        inkDamping: 0.55)

    static func generate(from profile: Profile, hue: Double, saturation scale: Double) -> PaletteTokens {
        func role(_ r: Role, damped: Bool = false) -> String {
            let s = r.saturation * scale * (damped ? profile.inkDamping : 1)
            return hexFromHSL(hue: hue, saturation: s, lightness: r.lightness)
        }

        let onAccent = role(profile.onAccent, damped: true)
        // HSL lightness is not perceived lightness. Blues and purples come out
        // markedly darker than yellows and greens at the same L, so holding L
        // fixed across hues would let some families' accents fall below the
        // contrast the reference palette was designed around — purple's dark
        // accent landed at 4.39:1, just under AA, entirely invisibly.
        // Lifting until the pair clears keeps "the accent always carries its
        // own text" a property of the system rather than something each new
        // hue has to be checked for by hand.
        let accent = liftUntilLegible(
            base: profile.accent, hue: hue,
            saturation: profile.accent.saturation * scale,
            against: onAccent)

        return PaletteTokens(
            background: role(profile.background),
            surface: role(profile.surface),
            tint: role(profile.tint),
            well: role(profile.well),
            border: role(profile.border),
            // Ink stays ink: the outline and body text carry only a hint of the
            // family so they read as near-black / near-white, not as coloured.
            outline: role(profile.outline, damped: true),
            accent: accent,
            onAccent: onAccent,
            selection: role(profile.selection),
            text: role(profile.text, damped: true),
            textSecondary: role(profile.textSecondary, damped: true))
    }

    /// Raises lightness in small steps until the pair clears WCAG AA, with a
    /// little headroom so rounding can't tip it back under.
    private static func liftUntilLegible(
        base: Role, hue: Double, saturation: Double, against foreground: String
    ) -> String {
        var lightness = base.lightness
        var candidate = hexFromHSL(hue: hue, saturation: saturation, lightness: lightness)
        var steps = 0
        while contrastRatio(candidate, foreground) < 4.6, lightness < 0.92, steps < 40 {
            lightness += 0.01
            candidate = hexFromHSL(hue: hue, saturation: saturation, lightness: lightness)
            steps += 1
        }
        return candidate
    }

    /// WCAG relative-luminance contrast ratio.
    static func contrastRatio(_ a: String, _ b: String) -> Double {
        func luminance(_ hex: String) -> Double {
            let (r, g, b) = rgb(from: hex)
            func linear(_ c: Double) -> Double {
                c <= 0.03928 ? c / 12.92 : pow((c + 0.055) / 1.055, 2.4)
            }
            return 0.2126 * linear(r) + 0.7152 * linear(g) + 0.0722 * linear(b)
        }
        let first = luminance(a), second = luminance(b)
        return (max(first, second) + 0.05) / (min(first, second) + 0.05)
    }

    // MARK: - Colour maths

    /// HSL → `#RRGGBB`. Written out rather than pulled from AppKit because the
    /// tokens have to be plain strings: they're emitted as CSS for the start
    /// page as well as parsed back into `NSColor` for the native UI.
    static func hexFromHSL(hue: Double, saturation: Double, lightness: Double) -> String {
        let h = ((hue.truncatingRemainder(dividingBy: 360)) + 360)
            .truncatingRemainder(dividingBy: 360) / 360
        let s = min(max(saturation, 0), 1)
        let l = min(max(lightness, 0), 1)

        guard s > 0 else {
            let v = Int((l * 255).rounded())
            return String(format: "#%02X%02X%02X", v, v, v)
        }

        let q = l < 0.5 ? l * (1 + s) : l + s - l * s
        let p = 2 * l - q

        func channel(_ offset: Double) -> Double {
            var t = h + offset
            if t < 0 { t += 1 }
            if t > 1 { t -= 1 }
            if t < 1.0 / 6 { return p + (q - p) * 6 * t }
            if t < 1.0 / 2 { return q }
            if t < 2.0 / 3 { return p + (q - p) * (2.0 / 3 - t) * 6 }
            return p
        }

        return String(format: "#%02X%02X%02X",
                      Int((channel(1.0 / 3) * 255).rounded()),
                      Int((channel(0) * 255).rounded()),
                      Int((channel(-1.0 / 3) * 255).rounded()))
    }

    /// Parses `#RRGGBB` back into components, for building `NSColor`.
    static func rgb(from hex: String) -> (r: Double, g: Double, b: Double) {
        let cleaned = hex.hasPrefix("#") ? String(hex.dropFirst()) : hex
        guard cleaned.count == 6, let value = UInt32(cleaned, radix: 16) else {
            return (0, 0, 0)
        }
        return (Double((value >> 16) & 0xFF) / 255,
                Double((value >> 8) & 0xFF) / 255,
                Double(value & 0xFF) / 255)
    }
}

extension PaletteTokens {
    /// The accent as a *foreground* — a selected sidebar glyph, a link, an
    /// active tab's icon.
    ///
    /// `accent` itself is tuned to carry dark text, which makes it far too
    /// light to be text: the original yellow is under 2:1 on its own surface.
    /// This walks the accent toward the text colour until it clears AA against
    /// `surface` and `selection`, so it keeps the family's hue while staying readable. In dark
    /// appearance the accent usually passes untouched.
    var accentInk: String {
        var candidate = accent
        var step = 0.0
        // Checked against `selection` too: a selected sidebar row draws this
        // glyph on the selection plate, which is darker than the surface.
        func legible(_ c: String) -> Bool {
            PaletteTokens.contrastRatio(c, surface) >= 4.6
                && PaletteTokens.contrastRatio(c, selection) >= 4.6
        }
        while !legible(candidate), step < 1 {
            step += 0.05
            candidate = PaletteTokens.mix(accent, text, step)
        }
        return candidate
    }

    /// Linear sRGB-space blend of two `#RRGGBB` colours.
    static func mix(_ a: String, _ b: String, _ amount: Double) -> String {
        let x = rgb(from: a), y = rgb(from: b)
        let t = min(max(amount, 0), 1)
        func channel(_ p: Double, _ q: Double) -> Int { Int(((p + (q - p) * t) * 255).rounded()) }
        return String(format: "#%02X%02X%02X",
                      channel(x.r, y.r), channel(x.g, y.g), channel(x.b, y.b))
    }
}

extension PaletteTokens {
    /// Whether this is the dark half of a family.
    var isDark: Bool {
        let (r, g, b) = PaletteTokens.rgb(from: background)
        return 0.2126 * r + 0.7152 * g + 0.0722 * b < 0.5
    }

    /// Resting fill of a neutral control — field, segmented track, secondary
    /// button. In light appearance the raw `well` is a heavy beige that made
    /// every control look like a printed label; pulling it toward the surface
    /// gives the faint, recessed grey-beige a native control has.
    var control: String {
        isDark ? well : PaletteTokens.mix(well, surface, 0.45)
    }

    /// The plate a selected segment sits on. It has to read as *raised* above
    /// `control`: in light that's the bright surface, but in dark the surface
    /// is darker than the track, which made the selection look sunken — so
    /// dark lifts the well toward the text colour instead.
    var raised: String {
        isDark ? PaletteTokens.mix(well, text, 0.13) : surface
    }
}
