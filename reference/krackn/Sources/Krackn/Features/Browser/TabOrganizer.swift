import Foundation

/// Everything the organiser and the auto-closer need to know about a tab,
/// without dragging a `WKWebView` along. Splitting this out is what makes both
/// policies testable — otherwise checking "does a 7-day rule spare a tab
/// touched an hour ago" would mean standing up a real web view.
struct TabSnapshot: Equatable {
    let id: UUID
    let title: String
    /// Registrable domain, e.g. `google.com` for `docs.google.com`.
    let domain: String
    /// Which dropdown the tab joins, and what that dropdown is called — the
    /// domain, except where one domain is many products (`SiteGroup`).
    let group: String
    let groupName: String
    let lastAccessed: Date
    let createdAt: Date
    let isPinned: Bool
    let isActive: Bool

    init(id: UUID, title: String, domain: String, group: String? = nil, groupName: String? = nil,
         lastAccessed: Date, createdAt: Date, isPinned: Bool = false, isActive: Bool = false) {
        self.id = id
        self.title = title
        self.domain = domain
        self.group = group ?? domain
        self.groupName = groupName ?? group ?? domain
        self.lastAccessed = lastAccessed
        self.createdAt = createdAt
        self.isPinned = isPinned
        self.isActive = isActive
    }
}

/// Which dropdown a page belongs to.
///
/// One per domain — except Google, where one domain is a dozen unrelated
/// products: a Doc, a Gmail inbox, a Gemini chat and a search all report
/// `google.com`, so they piled into one "google.com" dropdown. Google's
/// products each get their own, named for the product. Docs, Sheets and
/// Slides even share a host (`docs.google.com`), so the path decides those.
enum SiteGroup {
    struct Group: Equatable {
        /// What tabs are grouped by.
        let key: String
        /// What the dropdown is called.
        let name: String
    }

    /// `domain` is the host's registrable domain (`BrowserTab.registrableDomain`).
    static func of(host rawHost: String, path: String, domain: String) -> Group {
        let host = rawHost.lowercased()
        guard isGoogle(domain) else { return Group(key: domain, name: domain) }
        let sub = host.hasSuffix("." + domain) ? String(host.dropLast(domain.count + 1)) : ""
        let first = path.split(separator: "/").first.map { $0.lowercased() } ?? ""

        if sub == "docs" {
            let editor = docsEditors[first]
            return Group(key: "docs." + domain + "/" + (editor == nil ? "" : first),
                         name: editor ?? "Google Docs")
        }
        if sub.isEmpty || sub == "www" {
            if first == "maps" { return Group(key: domain + "/maps", name: "Google Maps") }
            return Group(key: domain, name: "Google")
        }
        // The product's own subdomain; an unknown one is named by its host.
        return Group(key: sub + "." + domain, name: products[sub] ?? host)
    }

    /// google.com and its country domains — google.de, google.co.uk,
    /// google.com.au — and nothing else that merely starts with "google.".
    static func isGoogle(_ domain: String) -> Bool {
        guard domain.hasPrefix("google.") else { return false }
        let rest = domain.dropFirst("google.".count).split(separator: ".", omittingEmptySubsequences: false)
        let cc = { (part: Substring) in part.count == 2 && part.allSatisfy(\.isLetter) }
        switch rest.count {
        case 1: return rest[0] == "com" || cc(rest[0])
        case 2: return (rest[0] == "co" || rest[0] == "com") && cc(rest[1])
        default: return false
        }
    }

    private static let docsEditors: [String: String] = [
        "document": "Google Docs", "spreadsheets": "Google Sheets",
        "presentation": "Google Slides", "forms": "Google Forms",
        "drawings": "Google Drawings",
    ]

    private static let products: [String: String] = [
        "mail": "Gmail", "drive": "Google Drive", "gemini": "Gemini",
        "calendar": "Google Calendar", "meet": "Google Meet", "maps": "Google Maps",
        "photos": "Google Photos", "keep": "Google Keep", "classroom": "Google Classroom",
        "translate": "Google Translate", "sites": "Google Sites", "chat": "Google Chat",
        "contacts": "Google Contacts", "news": "Google News", "scholar": "Google Scholar",
        "accounts": "Google Account", "myaccount": "Google Account", "notebooklm": "NotebookLM",
        "play": "Google Play", "earth": "Google Earth", "books": "Google Books",
        "jamboard": "Jamboard", "voice": "Google Voice", "groups": "Google Groups",
        "analytics": "Google Analytics", "ads": "Google Ads", "console.cloud": "Google Cloud",
        "cloud": "Google Cloud", "colab.research": "Colab", "aistudio": "Google AI Studio",
    ]
}

/// How the tab strip keeps itself in order.
enum TabOrganization: String, CaseIterable, Identifiable {
    case manual
    case domain
    case alphabetical
    case recentlyUsed
    case newest

    var id: String { rawValue }

    var label: String {
        switch self {
        case .manual: return "Manual"
        case .domain: return "By site"
        case .alphabetical: return "A–Z"
        case .recentlyUsed: return "Last opened"
        case .newest: return "Newest first"
        }
    }

    var detail: String {
        switch self {
        case .manual: return "Tabs stay wherever you drag them."
        case .domain: return "Tabs from the same site stay together. Drag one to move its site."
        case .alphabetical: return "Sorted by page title."
        case .recentlyUsed: return "Most recently viewed first."
        case .newest: return "Most recently opened first."
        }
    }

    /// What dragging a tab does under each strategy.
    ///
    /// Manual: it goes where you drop it. By site: tabs stay with their site,
    /// but grouping follows the order sites first appear — so dragging a tab
    /// moves its whole site's group, and dragging within a group reorders it.
    /// The sorted strategies have no room for a hand-placed tab, so a drag
    /// there switches the strip to manual (and says so) rather than snapping
    /// back, which reads as broken.
    var dragSwitchesToManual: Bool {
        switch self {
        case .manual, .domain: return false
        case .alphabetical, .recentlyUsed, .newest: return true
        }
    }
}

enum TabOrganizer {

    /// Returns the ids in the order the strip should show them.
    ///
    /// Pinned tabs are never touched: they hold their relative order and stay
    /// at the front, which is the whole point of pinning. Only the unpinned
    /// remainder is sorted.
    static func order(_ tabs: [TabSnapshot], by strategy: TabOrganization) -> [UUID] {
        let pinned = tabs.filter(\.isPinned)
        let loose = tabs.filter { !$0.isPinned }

        let sorted: [TabSnapshot]
        switch strategy {
        case .manual:
            sorted = loose
        case .alphabetical:
            // Case- and diacritic-insensitive so "iPhone" and "Álvaro" land
            // where a person would look for them.
            sorted = loose.sorted {
                $0.title.compare($1.title, options: [.caseInsensitive, .diacriticInsensitive])
                    == .orderedAscending
            }
        case .recentlyUsed:
            sorted = loose.sorted { $0.lastAccessed > $1.lastAccessed }
        case .newest:
            sorted = loose.sorted { $0.createdAt > $1.createdAt }
        case .domain:
            sorted = groupByDomain(loose)
        }

        return (pinned + sorted).map(\.id)
    }

    /// One entry per site, for the Dropdown tab style: the strip shows a
    /// single tab for each site, and clicking it lists that site's pages.
    ///
    /// The *arrangement* still decides the order — this takes tabs already in
    /// their arranged order, so a site sits where its first page falls and its
    /// pages keep their order inside the dropdown. "Last opened" therefore puts
    /// the site you were just on first, "Newest" the one with the newest page.
    /// A–Z is the exception: there sites themselves sort by name, since "the
    /// site whose first title comes first" isn't an order anyone looks for.
    ///
    /// A tab with no site yet (a new tab) is its own entry, and pinned tabs
    /// aren't grouped; they stay as icons at the left.
    struct Stack: Equatable {
        /// The group (`TabSnapshot.group`): a domain, or a Google product.
        let domain: String
        let ids: [UUID]
        /// What the stack is called on screen.
        var name: String = ""
        /// Stable identity for animation: the group, or a new tab's own id.
        var key: String { domain.isEmpty ? (ids.first?.uuidString ?? "") : domain }
    }

    static func stacks(_ tabs: [TabSnapshot], alphabetical: Bool = false) -> [Stack] {
        var order: [String] = []
        var groups: [String: [UUID]] = [:]
        var names: [String: String] = [:]
        for tab in tabs where !tab.isPinned {
            let key = tab.group.isEmpty ? "new:\(tab.id.uuidString)" : tab.group
            if groups[key] == nil {
                groups[key] = []
                order.append(key)
                names[key] = tab.groupName
            }
            groups[key]?.append(tab.id)
        }
        let stacks = order.map { key in
            Stack(domain: key.hasPrefix("new:") ? "" : key, ids: groups[key] ?? [],
                  name: key.hasPrefix("new:") ? "" : names[key] ?? key)
        }
        guard alphabetical else { return stacks }
        // Sites by name; new tabs (no site) after them, in their own order.
        return stacks.filter { !$0.domain.isEmpty }.sorted {
            $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedAscending
        } + stacks.filter { $0.domain.isEmpty }
    }

    /// Dropdown style, a stack dragged along the strip: the stacks in their
    /// new order. Every page of the site moves with it, in its own order.
    static func movingStack(_ stacks: [[UUID]], from: Int, to: Int) -> [[UUID]] {
        guard stacks.indices.contains(from), stacks.indices.contains(to), from != to else { return stacks }
        var result = stacks
        result.insert(result.remove(at: from), at: to)
        return result
    }

    /// One id moved to `index` within a list — a page dragged inside a
    /// stack's dropdown. The index is where it ends up, clamped to the list.
    static func moving(_ id: UUID, in ids: [UUID], to index: Int) -> [UUID] {
        guard let from = ids.firstIndex(of: id) else { return ids }
        var result = ids
        result.remove(at: from)
        result.insert(id, at: min(max(index, 0), result.count))
        return result
    }

    /// Clumps same-site tabs together while keeping the strip predictable:
    /// sites appear in the order you first opened one, and within a site the
    /// tabs stay in the order you opened them. Sorting the groups
    /// alphabetically instead would make an existing tab jump across the strip
    /// whenever an unrelated site was opened.
    private static func groupByDomain(_ tabs: [TabSnapshot]) -> [TabSnapshot] {
        var order: [String] = []
        var groups: [String: [TabSnapshot]] = [:]

        for tab in tabs {
            // Tabs with no domain yet — a blank new tab — are their own bucket
            // so they don't all pile under the first real site.
            let key = tab.domain.isEmpty ? "\u{FFFF}new" : tab.domain
            if groups[key] == nil {
                groups[key] = []
                order.append(key)
            }
            groups[key]?.append(tab)
        }
        return order.flatMap { groups[$0] ?? [] }
    }
}

// MARK: - Auto-close

/// How long a tab may sit untouched before it's closed.
enum AutoCloseRule: String, CaseIterable, Identifiable {
    case hour
    case day
    case week
    case custom

    var id: String { rawValue }

    var label: String {
        switch self {
        case .hour: return "1 hour"
        case .day: return "1 day"
        case .week: return "7 days"
        case .custom: return "Custom"
        }
    }

    /// Seconds of inactivity, or nil for `custom` where the user sets it.
    var interval: TimeInterval? {
        switch self {
        case .hour: return 3600
        case .day: return 86_400
        case .week: return 604_800
        case .custom: return nil
        }
    }
}

struct AutoClosePolicy: Equatable {
    var enabled = false
    var rule: AutoCloseRule = .day
    /// Used only when `rule == .custom`.
    var customHours: Double = 12
    /// Independently of inactivity, sweep at a fixed time each day.
    var atTimeEnabled = false
    var hour = 3
    var minute = 0

    var interval: TimeInterval {
        rule.interval ?? max(customHours, 0.25) * 3600
    }

    var describedInterval: String {
        guard rule == .custom else { return rule.label }
        let hours = max(customHours, 0.25)
        if hours < 1 { return "\(Int(hours * 60)) minutes" }
        if hours == 1 { return "1 hour" }
        if hours.truncatingRemainder(dividingBy: 24) == 0 {
            let days = Int(hours / 24)
            return days == 1 ? "1 day" : "\(days) days"
        }
        return "\(Int(hours)) hours"
    }
}

enum TabLifecycle {

    /// Which tabs the sweeper should close.
    ///
    /// Three things are always spared, and each for a different reason: pinned
    /// tabs because being spared is what pinning means, the active tab because
    /// closing what someone is looking at is indefensible, and the final tab
    /// because a browser with no tabs is a broken browser.
    static func tabsToClose(
        _ tabs: [TabSnapshot],
        policy: AutoClosePolicy,
        now: Date
    ) -> [UUID] {
        guard policy.enabled else { return [] }

        let cutoff = now.addingTimeInterval(-policy.interval)
        var doomed: [UUID] = []
        var survivors = tabs.count

        for tab in tabs {
            guard survivors > 1 else { break }
            guard !tab.isPinned, !tab.isActive else { continue }
            guard tab.lastAccessed < cutoff else { continue }
            doomed.append(tab.id)
            survivors -= 1
        }
        return doomed
    }

    /// Everything eligible for the scheduled daily sweep — same exemptions,
    /// but age doesn't matter because the user asked for a clean slate at a
    /// particular time.
    static func tabsToCloseOnSchedule(_ tabs: [TabSnapshot]) -> [UUID] {
        var doomed: [UUID] = []
        var survivors = tabs.count
        for tab in tabs {
            guard survivors > 1 else { break }
            guard !tab.isPinned, !tab.isActive else { continue }
            doomed.append(tab.id)
            survivors -= 1
        }
        return doomed
    }

    /// Whether the daily sweep instant fell between two checks.
    ///
    /// Comparing against "is it 03:00 right now" would miss the sweep whenever
    /// the app happened not to tick on that exact minute — asleep, busy, or
    /// simply between timer fires. Asking whether the scheduled instant lies in
    /// the interval since the last check can't miss it.
    static func scheduleElapsed(
        policy: AutoClosePolicy,
        since last: Date,
        now: Date,
        calendar: Calendar = .current
    ) -> Bool {
        guard policy.enabled, policy.atTimeEnabled, now > last else { return false }

        var probe = calendar.startOfDay(for: last)
        // Walk each day the interval spans and check that day's scheduled
        // instant. An interval longer than a day therefore still fires once.
        while probe <= now {
            if let fire = calendar.date(bySettingHour: policy.hour,
                                        minute: policy.minute,
                                        second: 0, of: probe),
               fire > last, fire <= now {
                return true
            }
            guard let next = calendar.date(byAdding: .day, value: 1, to: probe) else { break }
            probe = next
        }
        return false
    }
}
