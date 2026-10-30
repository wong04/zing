import Foundation

/// What the browser tells sites it is.
///
/// This is why Google Docs was blurry. Krackn used to identify as a fixed
/// "Safari 17.6"; Google now treats that as an unsupported browser — it shows
/// a "no longer supported" banner — and serves a degraded document view whose
/// canvas has *half* a backing pixel per CSS pixel instead of two. That's text
/// drawn at a quarter of the Retina pixel count and stretched to fit, while
/// the DOM toolbar around it stays sharp. Measured against a public doc:
/// "Safari 17.6" gave a 0.5 backing ratio, the installed Safari's version gave
/// 2.0 and sharp text, on first load and reload alike. GPU canvas on or off
/// made no difference.
///
/// So the version is read from the Safari actually installed, at launch,
/// rather than written down — a constant is exactly what aged into the bug.
enum UserAgent {
    /// Safari's own format, with the given Safari version.
    static func safari(version: String) -> String {
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 "
            + "(KHTML, like Gecko) Version/\(version) Safari/605.1.15"
    }

    /// A plausible Safari version: digits and dots, starting with a number.
    static func isValidVersion(_ version: String) -> Bool {
        guard let first = version.first, first.isNumber else { return false }
        return version.allSatisfy { $0.isNumber || $0 == "." } && !version.hasSuffix(".")
    }

    /// The installed Safari's version, or nil if it can't be read.
    static func installedSafariVersion(
        infoPlist: String = "/Applications/Safari.app/Contents/Info.plist"
    ) -> String? {
        guard let info = NSDictionary(contentsOfFile: infoPlist),
              let version = info["CFBundleShortVersionString"] as? String,
              isValidVersion(version) else { return nil }
        return version
    }

    /// Matches the Safari on this Mac. Nil — WebKit's own identifier, which
    /// also renders Docs sharply — when there's no Safari to match.
    static let current: String? = installedSafariVersion().map(safari(version:))
}
