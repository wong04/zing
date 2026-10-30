import Foundation

/// Picks the icon a page declares for itself.
///
/// Icons used to be looked up per *site* — the registrable domain — so every
/// tab on google.com showed whichever Google icon loaded first: Gmail's on
/// Docs, Slides, Drive and Sites alike. Docs, Sheets and Slides even share
/// one host, docs.google.com. The page's own `<link rel="icon">` is the only
/// thing that says which product it is, so that's what decides the icon now.
enum FaviconChoice {
    struct Link: Equatable {
        let href: String
        /// The `rel` attribute, lowercased: "icon", "shortcut icon", "apple-touch-icon"…
        let rel: String
        /// The `sizes` attribute, e.g. "32x32" or "16x16 32x32" or "any".
        let sizes: String
        let type: String

        init(href: String, rel: String, sizes: String = "", type: String = "") {
            self.href = href
            self.rel = rel.lowercased()
            self.sizes = sizes.lowercased()
            self.type = type.lowercased()
        }
    }

    /// Tab icons draw at 14–16pt, so 32px (2x) is ideal. Prefers a real
    /// `icon` over an apple-touch-icon (a big, padded, often differently
    /// styled tile), the size nearest 32 among those, and an SVG or "any"
    /// size when nothing is labelled — then the page's own order.
    static func best(_ links: [Link]) -> String? {
        let usable = links.filter { link in
            guard !link.href.isEmpty else { return false }
            let lower = link.href.lowercased()
            return lower.hasPrefix("https://") || lower.hasPrefix("http://")
                || lower.hasPrefix("data:image/")
        }
        guard !usable.isEmpty else { return nil }
        func score(_ link: Link) -> Int {
            var s = 0
            let isTouch = link.rel.contains("apple-touch")
            if !isTouch, link.rel.contains("icon") { s += 1000 }
            if link.sizes == "any" || link.type.contains("svg") {
                s += 300
            } else if let px = largestSize(link.sizes) {
                // Closest to 32 wins; bigger than needed beats too small.
                s += px >= 32 ? 400 - min(px - 32, 300) : 200 - (32 - px) * 4
            } else {
                s += 250
            }
            return s
        }
        return usable.enumerated()
            .max { a, b in
                let sa = score(a.element), sb = score(b.element)
                return sa != sb ? sa < sb : a.offset > b.offset
            }?
            .element.href
    }

    /// "16x16 32x32" → 32.
    static func largestSize(_ sizes: String) -> Int? {
        sizes.split(separator: " ")
            .compactMap { token -> Int? in
                let parts = token.split(separator: "x")
                guard parts.count == 2, let w = Int(parts[0]) else { return nil }
                return w
            }
            .max()
    }
}
