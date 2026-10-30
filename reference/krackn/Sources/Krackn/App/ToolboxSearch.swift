import Foundation

/// Finding a tool by what you want to do, not by what it happens to be called.
///
/// Nobody looks for "Hashes" — they look for "sha256", or "checksum". Nobody
/// thinks "Open a Link" when a site is blocked; they think "unblock". Every
/// Toolbox item carries the words people actually reach for, and results are
/// ranked so the obvious answer is first and Return opens it.
///
/// Pure, over plain identifiers, so `Tests/toolbox` can pin the rankings.
enum ToolboxSearch {
    struct Entry: Equatable {
        /// `PanelContent.id`, e.g. "tool:Hashes".
        let id: String
        let title: String
        let group: String
        let keywords: [String]
    }

    /// Best first. Entries that don't match are left out; ties keep catalog
    /// order, so related tools stay together.
    static func rank(_ query: String, _ entries: [Entry]) -> [Entry] {
        let tokens = query.lowercased()
            .split(whereSeparator: { $0 == " " || $0 == "-" || $0 == "_" })
            .map(String.init)
        guard !tokens.isEmpty else { return entries }
        return entries.enumerated()
            .compactMap { index, entry -> (Entry, Int, Int)? in
                guard let s = score(tokens, entry) else { return nil }
                return (entry, s, index)
            }
            .sorted { $0.1 != $1.1 ? $0.1 > $1.1 : $0.2 < $1.2 }
            .map(\.0)
    }

    /// Every word typed has to match something; the total is the sum of each
    /// word's best match, so "url encode" beats "url" alone on URL Encoding.
    static func score(_ tokens: [String], _ entry: Entry) -> Int? {
        let full = tokens.joined(separator: " ")
        // A phrase that matches whole ("remove background") outranks the
        // same words matched one at a time somewhere else.
        var bonus = 0
        if tokens.count > 1, let phrase = best(full, entry) { bonus = phrase }
        var total = bonus
        for token in tokens {
            guard let s = best(token, entry) else { return nil }
            total += s
        }
        return total
    }

    private static func best(_ q: String, _ entry: Entry) -> Int? {
        let title = entry.title.lowercased()
        let words = title.split(whereSeparator: { !$0.isLetter && !$0.isNumber }).map(String.init)
        let keys = entry.keywords.map { $0.lowercased() }
        var s: Int?
        func take(_ v: Int) { s = max(s ?? 0, v) }

        if title == q { take(100) }
        if title.hasPrefix(q) { take(90) }
        if words.contains(where: { $0.hasPrefix(q) }) { take(80) }
        if keys.contains(q) { take(75) }
        if keys.contains(where: { $0.hasPrefix(q) }) { take(65) }
        if q.count >= 2, title.contains(q) { take(50) }
        if entry.group.lowercased().hasPrefix(q) { take(30) }
        if q.count >= 3, keys.contains(where: { $0.contains(q) }) { take(25) }
        // Typing the start of each word — "gc" for Graphing Calculator,
        // "jwtd" for JWT Decoder — is how people abbreviate.
        if q.count >= 2, isSubsequence(q, of: title.filter { !$0.isWhitespace }) { take(10) }
        return s
    }

    private static func isSubsequence(_ needle: String, of haystack: String) -> Bool {
        var it = haystack.makeIterator()
        for c in needle {
            var found = false
            while let h = it.next() { if h == c { found = true; break } }
            if !found { return false }
        }
        return true
    }

    /// What people type when they want each feature. Keyed by `PanelContent.id`.
    static let keywords: [String: [String]] = [
        "section:Files": ["preview", "open", "view", "viewer", "pdf", "image", "video", "3d",
                          "stl", "model", "quicklook"],
        "section:Convert": ["convert", "format", "mp4", "mp3", "png", "jpg", "jpeg", "heic",
                            "webp", "gif", "pdf", "docx", "obj", "stl", "compress", "resize",
                            "ffmpeg", "export"],
        "section:Download": ["youtube", "video", "audio", "save page", "yt-dlp", "music",
                             "save video", "rip"],
        "tool:Background Remover": ["remove background", "cutout", "transparent", "photo",
                                    "erase background", "mask"],
        "section:Open a Link": ["unblock", "blocked", "relay", "proxy", "bypass", "reader",
                                "censored"],
        "section:Diagnose": ["blocked", "why", "test site", "censorship", "firewall", "dns",
                             "troubleshoot"],
        "section:Network & VPN": ["vpn", "dns", "doh", "wireguard", "profile", "encrypted dns",
                                  "tunnel"],
        "tool:Link Cleaner": ["tracking", "utm", "clean url", "strip", "shorten"],
        "section:Security": ["malware", "phishing", "virus", "scan", "gatekeeper", "xprotect",
                             "safe", "antivirus", "check file"],
        "tool:Passwords": ["password", "generate password", "passphrase", "random",
                           "strength"],
        "tool:Hashes": ["sha256", "sha1", "sha512", "md5", "checksum", "hash", "digest"],
        "tool:HMAC": ["signature", "sign", "mac", "sha256", "webhook"],
        "tool:JWT Decoder": ["jwt", "token", "bearer", "auth", "claims", "decode token"],
        "tool:JSON / YAML": ["json", "yaml", "format", "pretty", "prettify", "minify",
                             "validate", "beautify"],
        "tool:Base64": ["base64", "b64", "encode", "decode"],
        "tool:URL Encoding": ["percent", "encode", "decode", "escape", "query string",
                              "urlencode"],
        "tool:Regex Tester": ["regex", "regexp", "regular expression", "pattern", "match"],
        "tool:UUID": ["guid", "uuid", "identifier", "random id", "unique id"],
        "tool:Timestamps": ["epoch", "unix time", "date", "time", "iso 8601", "timestamp"],
        "tool:Cron Parser": ["cron", "crontab", "schedule"],
        "tool:Scientific Calculator": ["calculator", "math", "sin", "cos", "log", "compute",
                                       "arithmetic"],
        "tool:Graphing Calculator": ["graph", "plot", "function", "desmos", "curve", "chart"],
        "tool:Unit Converter": ["units", "metric", "imperial", "temperature", "length",
                                "weight", "celsius", "fahrenheit", "miles", "km"],
        "tool:Number Bases": ["hex", "hexadecimal", "binary", "octal", "decimal", "base"],
        "tool:Text Case": ["camelcase", "snake case", "uppercase", "lowercase", "title case",
                           "kebab", "capitalize"],
        "tool:Text Stats": ["word count", "characters", "reading time", "count", "words"],
        "tool:Color Converter": ["colour", "color", "hex", "rgb", "hsl", "picker", "palette"],
        "tool:QR Code": ["qr", "barcode", "code", "share link"],
    ]
}
