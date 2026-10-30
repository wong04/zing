import Foundation
import CryptoKit
import CoreImage
import CoreImage.CIFilterBuiltins
import AppKit

/// Pure functions behind the Utilities tab. Kept separate from the views so
/// each one is small enough to reason about on its own.
enum UtilityTools {

    // MARK: - Hashing

    enum HashAlgorithm: String, CaseIterable, Identifiable {
        case md5 = "MD5"
        case sha1 = "SHA-1"
        case sha256 = "SHA-256"
        case sha512 = "SHA-512"

        var id: String { rawValue }

        /// MD5 and SHA-1 are broken for anything security-related, but they're
        /// still what checksums on download pages use, so they stay available.
        var isLegacy: Bool { self == .md5 || self == .sha1 }
    }

    static func hash(_ text: String, using algorithm: HashAlgorithm) -> String {
        let data = Data(text.utf8)
        let digest: any Sequence<UInt8>
        switch algorithm {
        case .md5: digest = Insecure.MD5.hash(data: data)
        case .sha1: digest = Insecure.SHA1.hash(data: data)
        case .sha256: digest = SHA256.hash(data: data)
        case .sha512: digest = SHA512.hash(data: data)
        }
        return digest.map { String(format: "%02x", $0) }.joined()
    }

    static func hashFile(at url: URL, using algorithm: HashAlgorithm) throws -> String {
        let data = try Data(contentsOf: url, options: .mappedIfSafe)
        let digest: any Sequence<UInt8>
        switch algorithm {
        case .md5: digest = Insecure.MD5.hash(data: data)
        case .sha1: digest = Insecure.SHA1.hash(data: data)
        case .sha256: digest = SHA256.hash(data: data)
        case .sha512: digest = SHA512.hash(data: data)
        }
        return digest.map { String(format: "%02x", $0) }.joined()
    }

    // MARK: - Encoding

    static func base64Encode(_ text: String) -> String {
        Data(text.utf8).base64EncodedString()
    }

    static func base64Decode(_ text: String) -> String? {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        // Tolerate URL-safe base64 and missing padding, which is what you
        // usually get when pulling a value out of a JWT or a query string.
        var normalized = trimmed
            .replacingOccurrences(of: "-", with: "+")
            .replacingOccurrences(of: "_", with: "/")
        let remainder = normalized.count % 4
        if remainder > 0 {
            normalized += String(repeating: "=", count: 4 - remainder)
        }
        guard let data = Data(base64Encoded: normalized) else { return nil }
        return String(data: data, encoding: .utf8)
    }

    static func urlEncode(_ text: String) -> String {
        text.addingPercentEncoding(
            withAllowedCharacters: .alphanumerics.union(CharacterSet(charactersIn: "-._~"))
        ) ?? text
    }

    static func urlDecode(_ text: String) -> String {
        text.removingPercentEncoding ?? text
    }

    // MARK: - JSON

    /// Carries a human-readable parser message back to the UI without needing
    /// a full error hierarchy for one tool.
    struct JSONProblem: LocalizedError {
        let message: String
        var errorDescription: String? { message }
    }

    static func formatJSON(_ text: String, pretty: Bool) -> Result<String, JSONProblem> {
        guard let data = text.data(using: .utf8) else {
            return .failure(JSONProblem(message: "Couldn't read that as text."))
        }
        do {
            let object = try JSONSerialization.jsonObject(
                with: data, options: [.fragmentsAllowed])
            var options: JSONSerialization.WritingOptions = [.fragmentsAllowed]
            if pretty { options.insert(.prettyPrinted); options.insert(.sortedKeys) }
            let output = try JSONSerialization.data(withJSONObject: object, options: options)
            return .success(String(decoding: output, as: UTF8.self))
        } catch {
            return .failure(JSONProblem(message: error.localizedDescription))
        }
    }

    // MARK: - Passwords

    static func generatePassword(
        length: Int, symbols: Bool, digits: Bool, uppercase: Bool
    ) -> String {
        var alphabet = "abcdefghijkmnopqrstuvwxyz"   // no 'l'
        if uppercase { alphabet += "ABCDEFGHJKLMNPQRSTUVWXYZ" }  // no 'I', 'O'
        if digits { alphabet += "23456789" }                     // no '0', '1'
        if symbols { alphabet += "!@#$%^&*()-_=+[]{}" }

        let characters = Array(alphabet)
        // SystemRandomNumberGenerator is CSPRNG-backed on Apple platforms;
        // this is not the place for a seeded PRNG.
        var generator = SystemRandomNumberGenerator()
        return String((0..<length).map { _ in
            characters[Int(generator.next(upperBound: UInt64(characters.count)))]
        })
    }

    // MARK: - Link cleaning

    /// Query parameters that exist purely to track you. Stripping them makes a
    /// link shorter and stops it phoning home about where you found it.
    private static let trackingParameters: Set<String> = [
        "utm_source", "utm_medium", "utm_campaign", "utm_term", "utm_content",
        "utm_id", "utm_name", "utm_cid", "utm_reader", "utm_social",
        "fbclid", "gclid", "dclid", "gbraid", "wbraid", "msclkid", "twclid",
        "igshid", "igsh", "mc_cid", "mc_eid", "yclid", "_openstat",
        "ref", "ref_src", "ref_url", "referrer", "source",
        "si", "spm", "scm", "vero_id", "vero_conv", "wickedid",
        "oly_anon_id", "oly_enc_id", "__s", "trk", "trkCampaign",
        "ICID", "cmpid", "campaign_id", "ad_id", "adset_id",
    ]

    struct CleanedLink {
        let url: String
        let removed: [String]
    }

    static func cleanLink(_ input: String) -> CleanedLink? {
        guard let url = try? RelayEngine.normalize(input),
              var components = URLComponents(url: url, resolvingAgainstBaseURL: false)
        else { return nil }

        guard let items = components.queryItems, !items.isEmpty else {
            return CleanedLink(url: url.absoluteString, removed: [])
        }

        var removed: [String] = []
        let kept = items.filter { item in
            let isTracker = trackingParameters.contains(item.name)
                || item.name.hasPrefix("utm_")
            if isTracker { removed.append(item.name) }
            return !isTracker
        }

        components.queryItems = kept.isEmpty ? nil : kept
        return CleanedLink(url: components.string ?? url.absoluteString, removed: removed)
    }

    // MARK: - QR

    static func makeQRCode(from text: String, scale: CGFloat = 10) -> NSImage? {
        guard !text.isEmpty else { return nil }
        let filter = CIFilter.qrCodeGenerator()
        filter.message = Data(text.utf8)
        // M correction survives a bit of damage without bloating the code.
        filter.correctionLevel = "M"

        guard let output = filter.outputImage else { return nil }
        let scaled = output.transformed(by: CGAffineTransform(scaleX: scale, y: scale))

        let context = CIContext()
        guard let cgImage = context.createCGImage(scaled, from: scaled.extent) else {
            return nil
        }
        return NSImage(cgImage: cgImage, size: scaled.extent.size)
    }

    // MARK: - Case conversion

    enum CaseStyle: String, CaseIterable, Identifiable {
        case upper = "UPPER"
        case lower = "lower"
        case title = "Title Case"
        case camel = "camelCase"
        case snake = "snake_case"
        case kebab = "kebab-case"

        var id: String { rawValue }
    }

    static func convertCase(_ text: String, to style: CaseStyle) -> String {
        switch style {
        case .upper: return text.uppercased()
        case .lower: return text.lowercased()
        case .title: return text.capitalized
        case .camel:
            let parts = words(in: text)
            guard let first = parts.first else { return "" }
            return ([first.lowercased()] + parts.dropFirst().map(\.capitalized)).joined()
        case .snake: return words(in: text).map { $0.lowercased() }.joined(separator: "_")
        case .kebab: return words(in: text).map { $0.lowercased() }.joined(separator: "-")
        }
    }

    /// Splits on whitespace, punctuation, and camelCase humps, so all three
    /// naming conventions can round-trip into each other.
    private static func words(in text: String) -> [String] {
        var result: [String] = []
        var current = ""
        for character in text {
            if character.isWhitespace || character == "_" || character == "-" {
                if !current.isEmpty { result.append(current); current = "" }
            } else if character.isUppercase && !current.isEmpty
                        && current.last?.isUppercase == false {
                result.append(current)
                current = String(character)
            } else {
                current.append(character)
            }
        }
        if !current.isEmpty { result.append(current) }
        return result
    }

    // MARK: - Timestamps

    static func describeTimestamp(_ raw: String) -> [(String, String)] {
        let trimmed = raw.trimmingCharacters(in: .whitespaces)
        guard let value = Double(trimmed) else { return [] }
        // Anything past ~2001 in milliseconds is absurd as a seconds value,
        // so treat large numbers as ms — that's the usual JS timestamp.
        let seconds = value > 100_000_000_000 ? value / 1000 : value
        let date = Date(timeIntervalSince1970: seconds)

        let iso = ISO8601DateFormatter()
        iso.formatOptions = [.withInternetDateTime]

        let local = DateFormatter()
        local.dateStyle = .full
        local.timeStyle = .medium

        let relative = RelativeDateTimeFormatter()
        relative.unitsStyle = .full

        return [
            ("ISO 8601 (UTC)", iso.string(from: date)),
            ("Local time", local.string(from: date)),
            ("Relative", relative.localizedString(for: date, relativeTo: Date())),
            ("Unix seconds", String(Int(seconds))),
            ("Unix milliseconds", String(Int(seconds * 1000))),
        ]
    }
}
