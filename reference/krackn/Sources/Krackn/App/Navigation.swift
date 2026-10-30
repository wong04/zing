import SwiftUI
import AppKit

// MARK: - Where the main area is

/// What the main area shows. The sidebar is no longer a list of every section:
/// it holds the browser, the Toolbox, and whatever you've used lately. Every
/// other feature — Files and Convert included — lives in the Toolbox and is
/// reached from there, or from the sidebar once it's been used.
enum Route: Hashable {
    case browser
    case toolbox
    case feature(PanelContent)
}

// MARK: - The Toolbox catalog

/// How the Toolbox groups its contents: by the job you're doing, six groups
/// of four to seven, so each is one glance and the whole set fits on a screen.
enum ToolboxGroup: String, CaseIterable, Identifiable {
    case files = "Files & Media"
    case web = "Web & Network"
    case security = "Security"
    case developer = "Developer"
    case math = "Math & Science"
    case textDesign = "Text & Design"

    var id: String { rawValue }

    var icon: String {
        switch self {
        case .files: return "photo.on.rectangle"
        case .web: return "network"
        case .security: return "lock"
        case .developer: return "chevron.left.forwardslash.chevron.right"
        case .math: return "function"
        case .textDesign: return "textformat"
        }
    }

    var items: [PanelContent] {
        switch self {
        case .files:
            return [.section(.viewer), .section(.convert), .section(.download), .tool(.bgRemove)]
        case .web:
            return [.section(.unblock), .section(.diagnose), .section(.network), .tool(.linkCleaner)]
        case .security:
            return [.section(.security), .tool(.password), .tool(.hash), .tool(.hmac), .tool(.jwt)]
        case .developer:
            return [.tool(.json), .tool(.base64), .tool(.urlCode), .tool(.regex), .tool(.uuid),
                    .tool(.timestamp), .tool(.cron)]
        case .math:
            return [.tool(.calculator), .tool(.graphing), .tool(.units), .tool(.numberBase)]
        case .textDesign:
            return [.tool(.caseConvert), .tool(.textStats), .tool(.color), .tool(.qr)]
        }
    }

    /// Everything the Toolbox offers, in display order. Engine setup isn't
    /// here: it's configuration, so it lives in Settings.
    static var allItems: [PanelContent] { allCases.flatMap(\.items) }

    static func group(of item: PanelContent) -> ToolboxGroup? {
        allCases.first { $0.items.contains(item) }
    }

    /// The catalog as search entries.
    static var searchEntries: [ToolboxSearch.Entry] {
        allCases.flatMap { group in
            group.items.map { item in
                ToolboxSearch.Entry(id: item.id, title: item.title, group: group.rawValue,
                                    keywords: ToolboxSearch.keywords[item.id] ?? [])
            }
        }
    }
}

// MARK: - Recently used

/// The three features most recently opened, for the sidebar.
///
/// Stored as identifiers only — "Convert", "JSON" — never what was converted
/// or decoded, so remembering them across launches records nothing private.
@MainActor
final class RecentFeatures: ObservableObject {
    @Published private(set) var items: [PanelContent]

    init() {
        items = AppSettings.recentFeatures.compactMap(PanelContent.init(id:))
    }

    func touch(_ item: PanelContent) {
        guard ToolboxGroup.allItems.contains(item) else { return }
        save(RecentList.touching(item.id, in: items.map(\.id)))
    }

    func remove(_ item: PanelContent) {
        save(RecentList.removing(item.id, from: items.map(\.id)))
    }

    private func save(_ ids: [String]) {
        AppSettings.recentFeatures = ids
        withAnimation(Motion.standard) {
            items = ids.compactMap(PanelContent.init(id:))
        }
    }
}

// MARK: - Motion

/// The app's animation vocabulary. A handful of named curves used everywhere,
/// so the same kind of change always moves the same way — the consistency is
/// most of what makes motion feel designed rather than decorated.
///
/// Springs are critically or near-critically damped on purpose. A visible
/// bounce is the clearest single tell of a playful UI; Apple's own springs
/// settle with at most a hint of overshoot.
enum Motion {
    /// Selection, toggles, small state changes.
    static let quick = Animation.spring(response: 0.24, dampingFraction: 0.9)
    /// Most layout changes: rows appearing, pages switching.
    static let standard = Animation.spring(response: 0.34, dampingFraction: 0.88)
    /// Large surfaces arriving — the sidebar sliding out, a sheet opening.
    static let surface = Animation.spring(response: 0.42, dampingFraction: 0.86)
    /// Panels shoved aside by another panel. Slightly softer than `standard`
    /// so the pushed panel reads as yielding rather than snapping.
    static let yield = Animation.spring(response: 0.38, dampingFraction: 0.82)
    /// Hover highlights. Linear-ish and short: hover should never lag the cursor.
    static let hover = Animation.easeOut(duration: 0.12)
}

// MARK: - Glass

extension View {
    /// Frosted glass: blurs whatever is behind it within the window — web
    /// content included — tinted toward the palette so it stays warm rather
    /// than going system grey, with a hairline edge and a faint top highlight
    /// that gives the pane its thickness.
    func glass(radius: CGFloat, material: Material = .regularMaterial,
               tint: Double = 0.55, shadow: Bool = true) -> some View {
        let shape = RoundedRectangle(cornerRadius: radius, style: .continuous)
        return self
            .background {
                ZStack {
                    shape.fill(material)
                    shape.fill(Palette.tint.opacity(tint))
                }
            }
            .overlay {
                shape.strokeBorder(
                    LinearGradient(colors: [Color.white.opacity(0.16), Palette.separator,
                                            Palette.separator],
                                   startPoint: .top, endPoint: .bottom),
                    lineWidth: 1)
                    .allowsHitTesting(false)
            }
            .clipShape(shape)
            .shadow(color: .black.opacity(shadow ? 0.16 : 0), radius: 24, y: 10)
            .shadow(color: .black.opacity(shadow ? 0.06 : 0), radius: 2, y: 1)
    }
}

// MARK: - Sidebar presence

/// Whether the sidebar currently occupies the window's leading edge. When it
/// doesn't, the traffic lights sit over the content, so anything drawn along
/// the top-left — the browser's tab row, a page's top bar — has to step
/// around them.
private struct SidebarHiddenKey: EnvironmentKey { static let defaultValue = false }

extension EnvironmentValues {
    var sidebarHidden: Bool {
        get { self[SidebarHiddenKey.self] }
        set { self[SidebarHiddenKey.self] = newValue }
    }
}

enum WindowMetrics {
    /// Room the traffic lights need, measured from the window's leading edge.
    static let trafficLightsWidth: CGFloat = 84
}

/// Set on a feature shown in the main area under its `FeatureBar`. The bar
/// names the page and clears the traffic lights, so the page drops its own
/// title and most of its top margin.
private struct HostedUnderBarKey: EnvironmentKey { static let defaultValue = false }

extension EnvironmentValues {
    var hostedUnderBar: Bool {
        get { self[HostedUnderBarKey.self] }
        set { self[HostedUnderBarKey.self] = newValue }
    }
}

