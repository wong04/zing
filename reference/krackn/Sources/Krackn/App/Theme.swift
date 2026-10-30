import SwiftUI
import AppKit

/// Design language.
///
/// Started as an adaptation of a marketing site — outlined pills, oversized
/// headings, near-black strokes on everything — and was moved toward the
/// macOS idiom: hairline separators, soft elevation, continuous-curvature
/// rounded rectangles, a single type family at restrained sizes, and accent
/// used as a highlight rather than a fill. The warm palette stayed.
enum Palette {

    /// Builds a colour that resolves differently in light and dark appearance.
    /// Everything here is defined once, both ways, so the whole app follows the
    /// system setting without any per-view branching.
    private static func dynamic(light: NSColor, dark: NSColor) -> Color {
        Color(nsColor: NSColor(name: nil) { appearance in
            appearance.bestMatch(from: [.aqua, .darkAqua]) == .darkAqua ? dark : light
        })
    }

    private static func nsColor(_ hex: String) -> NSColor {
        let (r, g, b) = PaletteTokens.rgb(from: hex)
        return NSColor(srgbRed: r, green: g, blue: b, alpha: 1)
    }

    /// Resolves one role through whichever accent family is selected.
    ///
    /// These are computed rather than stored: the accent can change at runtime,
    /// and a `static let` would bake the launch-time palette in for the process
    /// lifetime. Every call site already reads `Palette.x` as a property, so
    /// making them computed costs an `NSColor` allocation per read and nothing
    /// else.
    private static func role(
        _ keyPath: KeyPath<PaletteTokens, String>
    ) -> Color {
        let accent = AccentTheme.current
        return dynamic(light: nsColor(accent.lightTokens[keyPath: keyPath]),
                       dark: nsColor(accent.darkTokens[keyPath: keyPath]))
    }

    /// The palette as CSS-ready hex, for web content the app renders.
    static var lightTokens: PaletteTokens { AccentTheme.current.lightTokens }
    static var darkTokens: PaletteTokens { AccentTheme.current.darkTokens }

    // MARK: - Surfaces

    /// The page itself. Warm off-white in light, deep warm brown in dark —
    /// never a neutral grey, which would read as cold next to the beige.
    static var background: Color { role(\.background) }

    /// Cards and grouped content, one step in from the background.
    static var surface: Color { role(\.surface) }

    /// The tinted band behind the sidebar and toolbars — the equivalent of the
    /// reference's pale green header.
    static var tint: Color { role(\.tint) }

    /// Inputs and inset wells.
    static var well: Color { role(\.well) }

    static var border: Color { role(\.border) }

    /// The strong, near-black outline used on buttons, cards and inputs — the
    /// defined "outlined" look. Dark ink on light, warm off-white on dark so it
    /// still reads as a crisp outline in both themes.
    static var outline: Color { role(\.outline) }

    // MARK: - Accent

    /// The family's signature tone. Light enough to carry dark text rather than
    /// white — that's what keeps buttons feeling soft instead of shouty.
    static var accent: Color { role(\.accent) }

    /// Sits on `accent`. Deliberately near-black, matching the reference's
    /// dark-on-bright treatment.
    static var onAccent: Color { role(\.onAccent) }

    /// A deeper tone for selected rows, where accent would be too loud.
    static var selection: Color { role(\.selection) }

    /// Accent as a readable foreground: selected glyphs, links, active icons.
    /// See `PaletteTokens.accentInk`.
    static var accentText: Color { role(\.accentInk) }

    // MARK: - Text

    static var text: Color { role(\.text) }
    static var textSecondary: Color { role(\.textSecondary) }

    // MARK: - Status

    /// Status colours stay fixed across accents. A red "danger" that turns
    /// green because the user picked the green theme would be actively
    /// dangerous in the security section.
    static let positive = dynamic(light: nsColor("#4F7A3F"), dark: nsColor("#8FBF7A"))
    static let caution = dynamic(light: nsColor("#A86A2E"), dark: nsColor("#D9A05B"))
    static let danger = dynamic(light: nsColor("#A33A2E"), dark: nsColor("#E08070"))

    // MARK: - Structure

    /// Hairline separators and control borders.
    ///
    /// The single biggest move from "drawn" to "refined". The original design
    /// outlined every card, button and field in near-black at 1.25–1.5pt, which
    /// reads as bold and playful — a cartoon edge. Apple's interfaces separate
    /// surfaces with hairlines at a fraction of that weight and let fill and
    /// soft shadow carry the depth. Everything structural uses this one token,
    /// so the weight of the whole app's linework can be tuned in one place.
    static var separator: Color { outline.opacity(0.13) }

    /// The resting fill of a neutral control — secondary buttons, the segmented
    /// track, text fields. One step off the surface it sits on.
    static var controlFill: Color { role(\.control) }

    /// A selected segment's plate — always lighter than `controlFill`.
    static var raised: Color { role(\.raised) }
}

/// Shape and spacing constants, so radii and rhythm are the same everywhere
/// instead of being re-picked per screen.
enum Metrics {
    /// Cards and grouped sections.
    static let cardRadius: CGFloat = 12
    /// Buttons, fields, menus, segmented tracks.
    static let controlRadius: CGFloat = 8
    /// Space between a section's small header and its card.
    static let sectionHeaderGap: CGFloat = 6
}

extension View {
    /// Soft elevation for a raised surface. Shadow, not outline, is what makes
    /// a card read as sitting above the page — the outline only draws an edge.
    func softElevation(_ strong: Bool = false) -> some View {
        shadow(color: .black.opacity(strong ? 0.10 : 0.05),
               radius: strong ? 10 : 6, x: 0, y: strong ? 3 : 1)
    }
}

// MARK: - Typography

/// Resolves and registers the app's typeface. The whole UI runs on Inter — a
/// clean grotesque in the spirit of the WhatsApp wordmark — bundled with the
/// app. If for any reason it fails to register we fall back to Helvetica Neue
/// (a grotesque on every Mac) rather than SF, so the app never quietly reverts
/// to the default system look.
enum AppFont {
    static let family: String = {
        register()
        let installed = NSFontManager.shared.availableFontFamilies
        if installed.contains("Inter") { return "Inter" }
        if installed.contains("Helvetica Neue") { return "Helvetica Neue" }
        return "Helvetica Neue"
    }()

    private static func register() {
        // The bundled .app carries Inter in Contents/Resources; the SwiftPM
        // dev build and the snapshot harness look beside the executable and in
        // the project. First hit wins.
        var candidates: [URL] = []
        if let inBundle = Bundle.main.url(forResource: "Inter", withExtension: "ttf") {
            candidates.append(inBundle)
        }
        let exe = Bundle.main.bundleURL
        candidates.append(exe.appendingPathComponent("Contents/Resources/Inter.ttf"))
        candidates.append(URL(fileURLWithPath: "Resources/Inter.ttf"))

        for url in candidates where FileManager.default.fileExists(atPath: url.path) {
            CTFontManagerRegisterFontsForURL(url as CFURL, .process, nil)
            break
        }
    }
}

extension Font {
    /// The one entry point for type. Everything in the app goes through this so
    /// the family is defined in exactly one place.
    ///
    /// SF Pro, the system face. Inter was the original choice, but next to the
    /// window chrome, menus and alerts — all SF — it read as a web page living
    /// inside a Mac app, and its regular weight is visibly heavier. SF also
    /// switches optical sizes (Text below 20pt, Display above) on its own,
    /// which is a large part of why Apple's type looks crisp at every size.
    static func app(_ size: CGFloat, _ weight: Font.Weight = .regular) -> Font {
        .system(size: size, weight: weight)
    }

    /// Big and heavy — the marketing-style page hero.
    static func display(_ size: CGFloat = 40) -> Font { .app(size, .bold) }

    static let sectionTitle = Font.app(17, .semibold)
    static let bodyText = Font.app(13.5, .regular)
}

// MARK: - Buttons

/// The `ButtonStyle` behind `.pill` / `.pillSecondary` / `.pillQuiet`, used
/// where a stock `Button` is styled rather than a `PillButton` built.
///
/// Rounded rectangles with continuous corners, not capsules: macOS controls
/// are softly squared, and a full capsule on every button is one of the
/// strongest "consumer web app" tells. No outline on the primary — the fill is
/// enough — and the secondary gets a neutral control fill with a hairline.
struct PillButtonStyle: ButtonStyle {
    enum Emphasis { case primary, secondary, quiet }

    var emphasis: Emphasis = .primary
    var compact = false

    @Environment(\.isEnabled) private var isEnabled

    func makeBody(configuration: Configuration) -> some View {
        let shape = RoundedRectangle(cornerRadius: Metrics.controlRadius, style: .continuous)
        return configuration.label
            .font(.app(compact ? 12 : 13, .medium))
            .foregroundStyle(foreground)
            .padding(.vertical, compact ? 4.5 : 7)
            .padding(.horizontal, compact ? 10 : 14)
            .background(background(pressed: configuration.isPressed), in: shape)
            .overlay(shape.strokeBorder(
                emphasis == .secondary ? Palette.separator : .clear, lineWidth: 1))
            .opacity(isEnabled ? 1 : 0.7)
            .animation(.easeOut(duration: 0.1), value: configuration.isPressed)
    }

    private var foreground: Color {
        guard isEnabled else { return Palette.textSecondary }
        switch emphasis {
        case .primary: return Palette.onAccent
        case .secondary, .quiet: return Palette.text
        }
    }

    private func background(pressed: Bool) -> Color {
        switch emphasis {
        // Same as `PillButton`: a disabled primary goes neutral.
        case .primary where !isEnabled: return Palette.controlFill
        case .primary: return pressed ? Palette.accent.opacity(0.82) : Palette.accent
        case .secondary: return pressed ? Palette.selection : Palette.controlFill
        case .quiet: return pressed ? Palette.controlFill : .clear
        }
    }
}

extension ButtonStyle where Self == PillButtonStyle {
    static var pill: PillButtonStyle { PillButtonStyle(emphasis: .primary) }
    static var pillSecondary: PillButtonStyle { PillButtonStyle(emphasis: .secondary) }
    static var pillQuiet: PillButtonStyle { PillButtonStyle(emphasis: .quiet) }
    static var pillCompact: PillButtonStyle {
        PillButtonStyle(emphasis: .secondary, compact: true)
    }
}

/// Borderless glyph button for toolbars — back, forward, reload, home.
///
/// Safari's toolbar glyphs have no resting background at all; a soft fill
/// appears only under the pointer or while pressed. A permanent filled circle
/// behind each one made the browser chrome look like a row of stickers.
struct CircleIconButtonStyle: ButtonStyle {
    var size: CGFloat = 28
    @Environment(\.isEnabled) private var isEnabled

    func makeBody(configuration: Configuration) -> some View {
        HoverGlyph(configuration: configuration, size: size, isEnabled: isEnabled)
    }

    private struct HoverGlyph: View {
        let configuration: ButtonStyleConfiguration
        let size: CGFloat
        let isEnabled: Bool
        @State private var hovering = false

        var body: some View {
            configuration.label
                .font(.system(size: size * 0.5, weight: .regular))
                .foregroundStyle(isEnabled ? Palette.text.opacity(0.78) : Palette.textSecondary)
                .frame(width: size, height: size)
                .background(
                    RoundedRectangle(cornerRadius: 6, style: .continuous)
                        .fill(configuration.isPressed ? Palette.selection
                              : (hovering && isEnabled ? Palette.controlFill : .clear)))
                .opacity(isEnabled ? 1 : 0.35)
                .contentShape(Rectangle())
                .onHover { hovering = $0 }
                .animation(.easeOut(duration: 0.12), value: hovering)
        }
    }
}

// MARK: - Containers

extension View {
    /// A raised card: surface fill, a hairline edge, and soft elevation.
    ///
    /// Previously a near-black 1.5pt stroke — the "drawn" look. Depth now comes
    /// from a faint shadow and the fill stepping off the page, the way grouped
    /// sections in System Settings sit.
    func cardSurface(radius: CGFloat = Metrics.cardRadius) -> some View {
        let shape = RoundedRectangle(cornerRadius: radius, style: .continuous)
        return self
            .background(Palette.surface, in: shape)
            .overlay(shape.strokeBorder(Palette.separator, lineWidth: 1))
            .softElevation()
    }

    /// An inset well for inputs and read-only values: control fill, hairline.
    func wellSurface(radius: CGFloat = Metrics.controlRadius) -> some View {
        let shape = RoundedRectangle(cornerRadius: radius, style: .continuous)
        return self
            .background(Palette.controlFill, in: shape)
            .overlay(shape.strokeBorder(Palette.separator, lineWidth: 1))
    }
}
