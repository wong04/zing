import Foundation

/// Decides whether what was typed is a place to go or something to search for.
///
/// One rule for every box that takes either — the address bar and the
/// new-tab page's search field. The new-tab field used to send everything to
/// the search engine, so typing `apple.com` searched for "apple.com"; and the
/// address bar's old test was "contains a dot", which sent `node.js` off to a
/// website that doesn't exist. Now: a scheme, `localhost`, an IP address, a
/// port, or a hostname ending in a real top-level domain means "go there";
/// anything else is a search. A bare name like `docs.google.com` or
/// `apple.com/iphone` needs no `https://`.
enum AddressInput {
    enum Result: Equatable {
        case url(URL)
        case search(String)
    }

    static func classify(_ raw: String) -> Result? {
        let text = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !text.isEmpty else { return nil }
        // Spaces mean words, not an address.
        if text.contains(where: { $0.isWhitespace }) { return .search(text) }

        // An explicit scheme — https://, file://, the app's own aiohome://.
        if text.contains("://") {
            return URL(string: text).map(Result.url) ?? .search(text)
        }
        let lower = text.lowercased()
        if lower.hasPrefix("about:") || lower.hasPrefix("mailto:") {
            return URL(string: text).map(Result.url) ?? .search(text)
        }

        // The host is everything before the first /, ? or #; a port may follow.
        let hostEnd = text.firstIndex { "/?#".contains($0) } ?? text.endIndex
        var host = String(text[..<hostEnd]).lowercased()
        var hasPort = false
        if let colon = host.lastIndex(of: ":") {
            let port = host[host.index(after: colon)...]
            guard !port.isEmpty, port.allSatisfy(\.isNumber) else { return .search(text) }
            host = String(host[..<colon])
            hasPort = true
        }

        // Local and numeric addresses are plain http, as browsers treat them.
        if host == "localhost" || isIPv4(host) {
            return URL(string: "http://" + text).map(Result.url) ?? .search(text)
        }

        let labels = host.split(separator: ".", omittingEmptySubsequences: false)
        guard labels.count >= 2,
              labels.allSatisfy({ !$0.isEmpty && $0.allSatisfy { $0.isLetter || $0.isNumber || $0 == "-" } }),
              let tld = labels.last
        else { return .search(text) }
        // A port is a strong enough signal on its own (`myserver.lan:8080`).
        guard hasPort || isTopLevelDomain(String(tld)) else { return .search(text) }
        return URL(string: "https://" + text).map(Result.url) ?? .search(text)
    }

    static func isIPv4(_ host: String) -> Bool {
        let parts = host.split(separator: ".", omittingEmptySubsequences: false)
        return parts.count == 4 && parts.allSatisfy { p in
            guard let n = Int(p), p.count <= 3 else { return false }
            return (0...255).contains(n)
        }
    }

    /// The real country-code domains, plus the generic ones people type. Not
    /// the full IANA list — 1,400 entries and growing — but enough that a real
    /// address is never searched, while `node.js`, `file.txt` and `index.html`
    /// still are. (Accepting *any* two letters looked tidy, and sent `node.js`
    /// to a website: `.js` isn't a country.)
    static func isTopLevelDomain(_ tld: String) -> Bool {
        let t = tld.lowercased()
        return countryTLDs.contains(t) || genericTLDs.contains(t)
    }

    static let countryTLDs: Set<String> = Set("""
    ac ad ae af ag ai al am ao aq ar as at au aw ax az ba bb bd be bf bg bh bi bj bm bn bo
    br bs bt bw by bz ca cc cd cf cg ch ci ck cl cm cn co cr cu cv cw cx cy cz de dj dk dm
    do dz ec ee eg er es et eu fi fj fk fm fo fr ga gd ge gf gg gh gi gl gm gn gp gq gr gs
    gt gu gw gy hk hm hn hr ht hu id ie il im in io iq ir is it je jm jo jp ke kg kh ki km
    kn kp kr kw ky kz la lb lc li lk lr ls lt lu lv ly ma mc md me mg mh mk ml mm mn mo mp
    mq mr ms mt mu mv mw mx my mz na nc ne nf ng ni nl no np nr nu nz om pa pe pf pg ph pk
    pl pm pn pr ps pt pw py qa re ro rs ru rw sa sb sc sd se sg sh si sk sl sm sn so sr ss
    st su sv sx sy sz tc td tf tg th tj tk tl tm tn to tr tt tv tw tz ua ug uk us uy uz va
    vc ve vg vi vn vu wf ws ye yt za zm zw
    """.split(whereSeparator: { $0.isWhitespace }).map(String.init))

    static let genericTLDs: Set<String> = [
        "com", "net", "org", "edu", "gov", "mil", "int", "info", "biz", "name", "pro",
        "io", "ai", "app", "dev", "page", "cloud", "tech", "online", "site", "website",
        "store", "shop", "blog", "news", "live", "art", "design", "xyz", "top", "club",
        "today", "world", "space", "studio", "media", "network", "systems", "software",
        "digital", "agency", "email", "solutions", "services", "academy", "school",
        "college", "university", "museum", "travel", "health", "law", "finance", "bank",
        "money", "games", "game", "fun", "wiki", "social", "chat", "video", "music",
        "photo", "photos", "gallery", "film", "tv", "radio", "zone", "one", "link",
        "click", "help", "support", "guide", "tips", "how", "new", "plus", "inc",
        "ltd", "llc", "gmbh", "group", "global", "earth", "city", "land", "moe",
        "dog", "cat", "eco", "green", "lol", "wtf", "rocks", "ninja", "codes",
        "google", "apple", "microsoft", "amazon", "youtube", "gle", "goog", "mozilla",
        "local", "lan", "home", "internal", "test", "localhost", "onion", "arpa", "coop", "aero",
    ]
}
