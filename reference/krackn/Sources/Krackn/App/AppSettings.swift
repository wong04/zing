import Foundation

/// Every persisted preference in one place.
///
/// This exists because the settings screen and the code that consumes a setting
/// used to declare the same `UserDefaults` key independently — and six of them
/// were declared only in the settings screen, so the controls wrote a value
/// nothing ever read. Routing both sides through here means a preference that
/// exists in the UI is a preference something acts on, and a typo is a compile
/// error rather than a silently dead toggle.
enum AppSettings {

    // MARK: - Keys

    enum Key {
        static let theme = "appTheme"
        static let accent = "aio.accent"
        static let homepage = "aio.homepage"
        /// The new-tab page's shortcut tiles, as JSON. Read and written only
        /// through `StartShortcuts` (absent = the built-in set).
        static let startShortcuts = "aio.startShortcuts"
        static let searchEngine = "aio.searchEngine"
        static let blockTrackers = "aio.blockTrackers"
        static let confirmQuit = "aio.confirmQuit"
        static let defaultQuality = "aio.defaultQuality"
        static let preserveOriginals = "aio.preserveOriginals"
        static let dohProvider = "aio.dohProvider"
        static let tabOrganization = "aio.tabOrganization"
        static let autoCloseEnabled = "aio.autoCloseEnabled"
        static let autoCloseRule = "aio.autoCloseRule"
        static let autoCloseCustomHours = "aio.autoCloseCustomHours"
        static let autoCloseAtTimeEnabled = "aio.autoCloseAtTimeEnabled"
        static let autoCloseHour = "aio.autoCloseHour"
        static let autoCloseMinute = "aio.autoCloseMinute"
        // There is deliberately no history key. Browsing runs on a
        // non-persistent data store, so there is no history to enable — a
        // switch offering one would be exactly the write-only control this
        // file exists to prevent.
        // Compatibility switches. These exist because the browser differs from
        // Safari in exactly three ways, and when a site renders or behaves
        // differently here those three are the entire suspect list. Making each
        // one flippable turns "which of these is it?" into a ten-second
        // experiment instead of a guess.
        static let persistentProfile = "aio.persistentProfile"
        static let defaultUserAgent = "aio.defaultUserAgent"
        static let acceleratedCanvas = "aio.acceleratedCanvas"
        static let restoreTabs = "aio.restoreTabs"
        static let securityRealtime = "aio.securityRealtime"
        static let securityDownloadScan = "aio.securityDownloadScan"
        static let securityDomainBlocking = "aio.securityDomainBlocking"
        static let securitySignatureCheck = "aio.securitySignatureCheck"
        // Navigation. The sidebar's pinned/low-profile state, and which
        // Toolbox features were used last — feature names only, never what
        // was done with them.
        static let sidebarPinned = "krackn.sidebarPinned"
        static let recentFeatures = "krackn.recentFeatures"
        static let showFavouritesBar = "krackn.showFavouritesBar"
        static let passwordAutofill = "krackn.passwordAutofill"
        /// Tab style: every tab separate, or one dropdown per site.
        static let tabDropdowns = "krackn.tabDropdowns"
    }

    private static var defaults: UserDefaults { .standard }

    /// Registers the shipping defaults so a first launch reads the same values
    /// the settings screen displays, rather than Foundation's zero values.
    /// "Stack by site" used to be an arrangement; it's now a tab style that
    /// works with any arrangement. Carry anyone who'd chosen it across.
    private static func migrateTabStyle() {
        guard defaults.string(forKey: Key.tabOrganization) == "stacked" else { return }
        defaults.set("domain", forKey: Key.tabOrganization)
        defaults.set(true, forKey: Key.tabDropdowns)
    }

    static func registerDefaults() {
        migrateTabStyle()
        defaults.register(defaults: [
            Key.searchEngine: SearchEngine.google.rawValue,
            Key.homepage: "",
            Key.blockTrackers: true,
            Key.confirmQuit: false,
            Key.defaultQuality: 80.0,
            Key.preserveOriginals: true,
            Key.dohProvider: "Cloudflare",
            Key.accent: AccentTheme.yellow.rawValue,
            Key.tabOrganization: "manual",
            Key.autoCloseEnabled: false,
            Key.autoCloseRule: "day",
            Key.autoCloseCustomHours: 12.0,
            Key.autoCloseAtTimeEnabled: false,
            Key.autoCloseHour: 3,
            Key.autoCloseMinute: 0,
            Key.persistentProfile: false,
            Key.defaultUserAgent: false,
            // On, WebKit's default. It was turned off on the theory that GPU canvas
            // blurred Google Docs; measured, it makes no difference — the blur was
            // the user agent (see `UserAgent`). On is faster on canvas-heavy pages.
            Key.acceleratedCanvas: true,
            // Off by default. Remembering open tabs means writing visited URLs
            // to disk, which is precisely the record this browser otherwise
            // refuses to keep — so it has to be asked for, not assumed.
            Key.restoreTabs: false,
            Key.securityRealtime: true,
            Key.securityDownloadScan: true,
            Key.securityDomainBlocking: true,
            Key.securitySignatureCheck: true,
        ])
    }

    // MARK: - Typed accessors

    static var searchEngine: SearchEngine {
        SearchEngine(rawValue: defaults.string(forKey: Key.searchEngine) ?? "") ?? .google
    }

    /// Empty means "use the local new-tab page", which is the default and the
    /// only homepage that makes no network request.
    static var homepage: String {
        (defaults.string(forKey: Key.homepage) ?? "")
            .trimmingCharacters(in: .whitespacesAndNewlines)
    }

    /// Reads a boolean whose *absence* must not mean `false`.
    ///
    /// `UserDefaults.bool(forKey:)` returns false for a key that was never
    /// written, so a protective setting read before `registerDefaults()` had
    /// run would silently come back as "off". That is exactly the wrong way for
    /// this to fail: it turned "never overwrite existing files" into "always
    /// overwrite" in any context that didn't go through app startup.
    static func flag(_ key: String, default fallback: Bool) -> Bool {
        defaults.object(forKey: key) == nil ? fallback : defaults.bool(forKey: key)
    }

    static var blockTrackers: Bool { flag(Key.blockTrackers, default: true) }
    static var confirmQuit: Bool { flag(Key.confirmQuit, default: false) }
    static var preserveOriginals: Bool { flag(Key.preserveOriginals, default: true) }

    /// Trades the no-trace guarantee for a browsing profile that persists.
    static var persistentProfile: Bool { flag(Key.persistentProfile, default: false) }
    /// Stops claiming to be Safari and sends WebKit's own user agent.
    static var defaultUserAgent: Bool { flag(Key.defaultUserAgent, default: false) }
    /// Whether canvas drawing runs on the GPU. Off means the CPU path, which
    /// rasterises at the display's real scale.
    static var acceleratedCanvas: Bool { flag(Key.acceleratedCanvas, default: true) }
    /// Reopen the tabs that were open last time. Trades the no-trace guarantee
    /// for continuity; see `SessionStore`.
    static var restoreTabs: Bool { flag(Key.restoreTabs, default: false) }

    static var tabDropdowns: Bool { flag(Key.tabDropdowns, default: false) }

    /// Offer the Mac's Passwords in web sign-in fields. See `KeychainAutofill`.
    static var passwordAutofill: Bool { flag(Key.passwordAutofill, default: true) }

    static var recentFeatures: [String] {
        get { defaults.stringArray(forKey: Key.recentFeatures) ?? [] }
        set { defaults.set(newValue, forKey: Key.recentFeatures) }
    }

    /// The security toggles, which are all on unless explicitly turned off.
    enum SecurityFlag: String {
        case securityRealtime, securityDownloadScan
        case securityDomainBlocking, securitySignatureCheck

        var key: String {
            switch self {
            case .securityRealtime: return Key.securityRealtime
            case .securityDownloadScan: return Key.securityDownloadScan
            case .securityDomainBlocking: return Key.securityDomainBlocking
            case .securitySignatureCheck: return Key.securitySignatureCheck
            }
        }
    }

    static func securityFlag(_ flag: SecurityFlag) -> Bool {
        self.flag(flag.key, default: true)
    }

    static var defaultQuality: Int {
        let raw = defaults.double(forKey: Key.defaultQuality)
        return Int(min(max(raw == 0 ? 80 : raw, 10), 100))
    }

    static var dohProvider: DoHResolver.Provider {
        DoHResolver.Provider(rawValue: defaults.string(forKey: Key.dohProvider) ?? "")
            ?? .cloudflare
    }

    // MARK: - Shared folders

    /// Where anything this app produces goes unless the user picks otherwise.
    ///
    /// Everything used to land somewhere different — converted files beside
    /// their source, cutouts beside the original, dropped web content in a temp
    /// directory. One predictable destination the user already knows how to find
    /// beats three clever ones they don't.
    static var downloadsDirectory: URL {
        // Lets the test harnesses download into a scratch folder instead of
        // the user's real Downloads.
        if let override = ProcessInfo.processInfo.environment["KRACKN_DOWNLOADS_DIR"] {
            return URL(fileURLWithPath: override, isDirectory: true)
        }
        return FileManager.default.urls(for: .downloadsDirectory, in: .userDomainMask).first
            ?? FileManager.default.homeDirectoryForCurrentUser
                .appendingPathComponent("Downloads", isDirectory: true)
    }
}

// MARK: - Search engines

/// The one definition of a search engine. `BrowserModel` and the settings
/// screen each had their own before, which is why choosing DuckDuckGo changed
/// the new-tab page but left the address bar on Google.
enum SearchEngine: String, CaseIterable, Identifiable {
    case google = "Google"
    case duckduckgo = "DuckDuckGo"
    case bing = "Bing"
    case brave = "Brave"
    case kagi = "Kagi"
    case startpage = "Startpage"

    var id: String { rawValue }

    /// Endpoint the new-tab page's `<form>` submits its `q` field to.
    var formAction: String {
        switch self {
        case .google: return "https://www.google.com/search"
        case .duckduckgo: return "https://duckduckgo.com/"
        case .bing: return "https://www.bing.com/search"
        case .brave: return "https://search.brave.com/search"
        case .kagi: return "https://kagi.com/search"
        case .startpage: return "https://www.startpage.com/sp/search"
        }
    }

    /// Builds a search URL for text typed into the address bar.
    func queryURL(for terms: String) -> URL? {
        guard var components = URLComponents(string: formAction) else { return nil }
        components.queryItems = [URLQueryItem(name: "q", value: terms)]
        return components.url
    }

    /// True for engines that don't profile you. Surfaced in Settings so the
    /// privacy claim elsewhere in the app stays consistent with the default.
    var isPrivate: Bool {
        switch self {
        case .duckduckgo, .brave, .startpage, .kagi: return true
        case .google, .bing: return false
        }
    }
}
