import Foundation

/// One tile on the new-tab page.
struct StartShortcut: Codable, Equatable, Identifiable {
    var title: String
    var url: String
    var id: String { url }
}

/// The new-tab page's tiles: which sites, in what order, under what names.
///
/// Yours to choose — Settings › New Tab, or "Edit" on the page itself. Stored
/// under `AppSettings.Key.startShortcuts`; until you change anything it holds
/// nothing, and the built-in set is shown.
enum StartShortcuts {
    /// Enough to fill four rows of the page's grid without it turning into a
    /// bookmarks list.
    static let limit = 16

    static let defaults: [StartShortcut] = [
        .init(title: "GitHub", url: "https://github.com"),
        .init(title: "Stack Overflow", url: "https://stackoverflow.com"),
        .init(title: "arXiv", url: "https://arxiv.org"),
        .init(title: "MDN", url: "https://developer.mozilla.org"),
        .init(title: "Wikipedia", url: "https://wikipedia.org"),
        .init(title: "Wolfram Alpha", url: "https://www.wolframalpha.com"),
        .init(title: "Desmos", url: "https://www.desmos.com/calculator"),
        .init(title: "Hacker News", url: "https://news.ycombinator.com"),
    ]

    /// The built-in tiles' own symbols; anything you add shows its initial.
    private static let glyphs: [String: String] = [
        "https://github.com": "◱", "https://stackoverflow.com": "◲",
        "https://arxiv.org": "✦", "https://developer.mozilla.org": "◳",
        "https://wikipedia.org": "◈", "https://www.wolframalpha.com": "∑",
        "https://www.desmos.com/calculator": "∿", "https://news.ycombinator.com": "▲",
    ]

    static func glyph(for shortcut: StartShortcut) -> String {
        if let glyph = glyphs[shortcut.url] { return glyph }
        return shortcut.title.trimmingCharacters(in: .whitespaces).first.map { String($0).uppercased() } ?? "•"
    }

    // MARK: Stored list

    static var current: [StartShortcut] {
        get { decode(UserDefaults.standard.string(forKey: AppSettings.Key.startShortcuts)) }
        set {
            UserDefaults.standard.set(encode(newValue), forKey: AppSettings.Key.startShortcuts)
        }
    }

    static func resetToDefaults() {
        UserDefaults.standard.removeObject(forKey: AppSettings.Key.startShortcuts)
    }

    /// Nothing stored, or something unreadable → the built-in set. Entries
    /// that aren't web addresses are dropped rather than rendered: the list
    /// ends up in a page's `href`s, and `javascript:` must never get there.
    static func decode(_ raw: String?) -> [StartShortcut] {
        guard let raw, let data = raw.data(using: .utf8),
              let list = try? JSONDecoder().decode([StartShortcut].self, from: data)
        else { return defaults }
        return Array(list.filter { isWebAddress($0.url) }.prefix(limit))
    }

    static func encode(_ list: [StartShortcut]) -> String {
        guard let data = try? JSONEncoder().encode(Array(list.prefix(limit))) else { return "[]" }
        return String(decoding: data, as: UTF8.self)
    }

    // MARK: Adding

    /// A shortcut from what was typed: the address by the same rule as the
    /// address bar (`apple.com` → https://apple.com), the name if one was
    /// given, else one made from the site. Nil if it isn't a web address.
    static func make(title rawTitle: String, address: String) -> StartShortcut? {
        guard case .url(let url)? = AddressInput.classify(address), isWebAddress(url.absoluteString)
        else { return nil }
        let title = rawTitle.trimmingCharacters(in: .whitespacesAndNewlines)
        return StartShortcut(title: title.isEmpty ? name(for: url) : String(title.prefix(40)),
                             url: url.absoluteString)
    }

    /// `www.apple.com` → "Apple", `docs.google.com` → "Docs".
    static func name(for url: URL) -> String {
        var host = url.host ?? url.absoluteString
        if host.hasPrefix("www.") { host.removeFirst(4) }
        let first = host.split(separator: ".").first.map(String.init) ?? host
        return first.prefix(1).uppercased() + first.dropFirst()
    }

    static func isWebAddress(_ string: String) -> Bool {
        guard let url = URL(string: string), let scheme = url.scheme?.lowercased() else { return false }
        return (scheme == "https" || scheme == "http") && url.host?.isEmpty == false
    }

    /// For putting your names and addresses into the page's HTML.
    static func escapeHTML(_ text: String) -> String {
        var out = ""
        for c in text {
            switch c {
            case "&": out += "&amp;"
            case "<": out += "&lt;"
            case ">": out += "&gt;"
            case "\"": out += "&quot;"
            case "'": out += "&#39;"
            default: out.append(c)
            }
        }
        return out
    }
}
