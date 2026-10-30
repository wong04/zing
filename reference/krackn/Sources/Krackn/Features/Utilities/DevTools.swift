import Foundation
import CryptoKit

/// Computations behind the engineering/science tools. Pure functions, kept
/// away from the views so each is easy to reason about and test.
enum DevTools {

    // MARK: - Number bases

    struct BaseConversion {
        let decimal: String
        let hex: String
        let binary: String
        let octal: String
        let bytes: String
    }

    /// Parses a value written in any common base (0x…, 0b…, 0o…, or decimal)
    /// and renders it in all of them.
    static func convertBase(_ input: String) -> BaseConversion? {
        let text = input.trimmingCharacters(in: .whitespaces)
            .replacingOccurrences(of: "_", with: "")
            .replacingOccurrences(of: " ", with: "")
        guard !text.isEmpty else { return nil }

        let value: Int
        let lower = text.lowercased()
        if lower.hasPrefix("0x") {
            guard let v = Int(lower.dropFirst(2), radix: 16) else { return nil }
            value = v
        } else if lower.hasPrefix("0b") {
            guard let v = Int(lower.dropFirst(2), radix: 2) else { return nil }
            value = v
        } else if lower.hasPrefix("0o") {
            guard let v = Int(lower.dropFirst(2), radix: 8) else { return nil }
            value = v
        } else {
            guard let v = Int(text) else { return nil }
            value = v
        }

        return BaseConversion(
            decimal: String(value),
            hex: "0x" + String(value, radix: 16).uppercased(),
            binary: "0b" + String(value, radix: 2),
            octal: "0o" + String(value, radix: 8),
            bytes: ByteCountFormatter.string(fromByteCount: Int64(value), countStyle: .binary)
        )
    }

    // MARK: - Colour

    struct ColorValues {
        let hex: String
        let rgb: String
        let rgbFloat: String
        let hsl: String
        let cmyk: String
    }

    /// Converts a hex or `rgb()` colour into every notation a designer or
    /// front-end engineer needs.
    static func convertColor(_ input: String) -> ColorValues? {
        var r = 0.0, g = 0.0, b = 0.0
        let text = input.trimmingCharacters(in: .whitespaces).lowercased()

        if text.hasPrefix("#") || text.range(of: "^[0-9a-f]{3,8}$", options: .regularExpression) != nil {
            var hex = text.hasPrefix("#") ? String(text.dropFirst()) : text
            // Expand shorthand (#abc -> #aabbcc).
            if hex.count == 3 {
                hex = hex.map { "\($0)\($0)" }.joined()
            }
            guard hex.count >= 6, let value = Int(hex.prefix(6), radix: 16) else { return nil }
            r = Double((value >> 16) & 0xFF)
            g = Double((value >> 8) & 0xFF)
            b = Double(value & 0xFF)
        } else if text.hasPrefix("rgb") {
            let numbers = text.components(separatedBy: CharacterSet(charactersIn: "0123456789.").inverted)
                .filter { !$0.isEmpty }.compactMap(Double.init)
            guard numbers.count >= 3 else { return nil }
            (r, g, b) = (numbers[0], numbers[1], numbers[2])
        } else {
            return nil
        }

        let hex = String(format: "#%02X%02X%02X", Int(r), Int(g), Int(b))
        let rn = r / 255, gn = g / 255, bn = b / 255

        // HSL
        let maxV = max(rn, gn, bn), minV = min(rn, gn, bn)
        let lightness = (maxV + minV) / 2
        var hue = 0.0, saturation = 0.0
        if maxV != minV {
            let delta = maxV - minV
            saturation = lightness > 0.5 ? delta / (2 - maxV - minV) : delta / (maxV + minV)
            switch maxV {
            case rn: hue = (gn - bn) / delta + (gn < bn ? 6 : 0)
            case gn: hue = (bn - rn) / delta + 2
            default: hue = (rn - gn) / delta + 4
            }
            hue /= 6
        }

        // CMYK
        let k = 1 - maxV
        let denominator = 1 - k
        let c = denominator == 0 ? 0 : (1 - rn - k) / denominator
        let m = denominator == 0 ? 0 : (1 - gn - k) / denominator
        let y = denominator == 0 ? 0 : (1 - bn - k) / denominator

        return ColorValues(
            hex: hex,
            rgb: "rgb(\(Int(r)), \(Int(g)), \(Int(b)))",
            rgbFloat: String(format: "%.3f, %.3f, %.3f", rn, gn, bn),
            hsl: "hsl(\(Int(hue * 360)), \(Int(saturation * 100))%, \(Int(lightness * 100))%)",
            cmyk: "cmyk(\(Int(c * 100))%, \(Int(m * 100))%, \(Int(y * 100))%, \(Int(k * 100))%)"
        )
    }

    // MARK: - Units

    enum UnitKind: String, CaseIterable, Identifiable {
        case length = "Length"
        case mass = "Mass"
        case temperature = "Temperature"
        case data = "Data"
        case pressure = "Pressure"
        case energy = "Energy"
        case angle = "Angle"

        var id: String { rawValue }

        /// Unit name → factor relative to the base unit for that kind.
        var units: [(String, Double)] {
            switch self {
            case .length:
                return [("m", 1), ("km", 1000), ("cm", 0.01), ("mm", 0.001),
                        ("µm", 1e-6), ("nm", 1e-9), ("in", 0.0254), ("ft", 0.3048),
                        ("yd", 0.9144), ("mi", 1609.344), ("thou", 2.54e-5)]
            case .mass:
                return [("kg", 1), ("g", 0.001), ("mg", 1e-6), ("t", 1000),
                        ("lb", 0.45359237), ("oz", 0.028349523125), ("st", 6.35029318)]
            case .temperature:
                return [("°C", 1), ("K", 1), ("°F", 1)]   // handled specially
            case .data:
                return [("B", 1), ("KB", 1024), ("MB", 1048576), ("GB", 1073741824),
                        ("TB", 1099511627776), ("bit", 0.125)]
            case .pressure:
                return [("Pa", 1), ("kPa", 1000), ("MPa", 1e6), ("bar", 100000),
                        ("psi", 6894.757), ("atm", 101325), ("mmHg", 133.322)]
            case .energy:
                return [("J", 1), ("kJ", 1000), ("cal", 4.184), ("kcal", 4184),
                        ("Wh", 3600), ("kWh", 3.6e6), ("eV", 1.602176634e-19), ("BTU", 1055.06)]
            case .angle:
                return [("deg", 1), ("rad", 57.2957795131), ("grad", 0.9), ("turn", 360)]
            }
        }
    }

    static func convertUnit(_ value: Double, from: String, to: String, kind: UnitKind) -> Double? {
        // Temperature is offset-based, so factors don't apply.
        if kind == .temperature {
            let celsius: Double
            switch from {
            case "°C": celsius = value
            case "K": celsius = value - 273.15
            case "°F": celsius = (value - 32) * 5 / 9
            default: return nil
            }
            switch to {
            case "°C": return celsius
            case "K": return celsius + 273.15
            case "°F": return celsius * 9 / 5 + 32
            default: return nil
            }
        }
        let table = Dictionary(uniqueKeysWithValues: kind.units)
        guard let fromFactor = table[from], let toFactor = table[to] else { return nil }
        return value * fromFactor / toFactor
    }

    // MARK: - Text / data

    /// Counts that matter when writing or preparing training data.
    struct TextStats {
        let characters: Int
        let charactersNoSpaces: Int
        let words: Int
        let lines: Int
        let sentences: Int
        let paragraphs: Int
        /// Rough LLM token estimate — about 4 characters per token in English.
        let approxTokens: Int
        let readingMinutes: Double
    }

    static func analyze(_ text: String) -> TextStats {
        let words = text.split { $0.isWhitespace || $0.isNewline }.count
        let lines = text.isEmpty ? 0 : text.components(separatedBy: .newlines).count
        let sentences = text.split(whereSeparator: { ".!?".contains($0) })
            .filter { !$0.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }.count
        let paragraphs = text.components(separatedBy: "\n\n")
            .filter { !$0.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }.count
        return TextStats(
            characters: text.count,
            charactersNoSpaces: text.filter { !$0.isWhitespace }.count,
            words: words,
            lines: lines,
            sentences: sentences,
            paragraphs: paragraphs,
            approxTokens: Int((Double(text.count) / 4).rounded()),
            readingMinutes: Double(words) / 220.0
        )
    }

    // MARK: - Cryptography

    /// HMAC over a message with a shared key — used constantly when signing
    /// API requests and webhooks.
    static func hmac(message: String, key: String, algorithm: UtilityTools.HashAlgorithm) -> String {
        let keyData = SymmetricKey(data: Data(key.utf8))
        let messageData = Data(message.utf8)
        let digest: any Sequence<UInt8>
        switch algorithm {
        case .md5: digest = HMAC<Insecure.MD5>.authenticationCode(for: messageData, using: keyData)
        case .sha1: digest = HMAC<Insecure.SHA1>.authenticationCode(for: messageData, using: keyData)
        case .sha256: digest = HMAC<SHA256>.authenticationCode(for: messageData, using: keyData)
        case .sha512: digest = HMAC<SHA512>.authenticationCode(for: messageData, using: keyData)
        }
        return digest.map { String(format: "%02x", $0) }.joined()
    }

    // MARK: - JWT

    struct JWTParts {
        let header: String
        let payload: String
        let signature: String
        let expiry: String?
    }

    /// Decodes a JWT without verifying it — for inspecting claims while
    /// debugging auth. Verification needs the signing key, which we don't have.
    static func decodeJWT(_ token: String) -> JWTParts? {
        let segments = token.trimmingCharacters(in: .whitespacesAndNewlines)
            .components(separatedBy: ".")
        guard segments.count >= 2 else { return nil }

        func decode(_ segment: String) -> String {
            guard let data = UtilityTools.base64Decode(segment) else { return "(unreadable)" }
            // Pretty-print if it parses as JSON.
            if case .success(let pretty) = UtilityTools.formatJSON(data, pretty: true) {
                return pretty
            }
            return data
        }

        let payload = decode(segments[1])
        // Surface `exp` in human terms, since that's the usual reason to look.
        var expiry: String?
        if let expRange = payload.range(of: "\"exp\"\\s*:\\s*([0-9]+)", options: .regularExpression) {
            let digits = payload[expRange].components(
                separatedBy: CharacterSet.decimalDigits.inverted).joined()
            if let seconds = Double(digits) {
                let date = Date(timeIntervalSince1970: seconds)
                let formatter = DateFormatter()
                formatter.dateStyle = .medium
                formatter.timeStyle = .medium
                expiry = "\(formatter.string(from: date))"
                    + (date < Date() ? "  (EXPIRED)" : "  (valid)")
            }
        }

        return JWTParts(
            header: decode(segments[0]),
            payload: payload,
            signature: segments.count > 2 ? segments[2] : "",
            expiry: expiry
        )
    }

    // MARK: - Cron

    /// Explains a cron expression in plain English — these are famously easy
    /// to get wrong and hard to read back.
    static func describeCron(_ expression: String) -> String? {
        let fields = expression.split(separator: " ").map(String.init)
        guard fields.count == 5 else { return nil }

        func describe(_ field: String, _ name: String, range: String) -> String {
            if field == "*" { return "every \(name)" }
            if field.hasPrefix("*/") { return "every \(field.dropFirst(2)) \(name)s" }
            if field.contains(",") { return "\(name) \(field)" }
            if field.contains("-") { return "\(name) \(field)" }
            return "\(name) \(field)"
        }

        let parts = [
            describe(fields[0], "minute", range: "0-59"),
            describe(fields[1], "hour", range: "0-23"),
            describe(fields[2], "day of month", range: "1-31"),
            describe(fields[3], "month", range: "1-12"),
            describe(fields[4], "day of week", range: "0-6"),
        ]
        return "Runs at " + parts.joined(separator: ", ") + "."
    }

    // MARK: - Regex

    /// Carries a parser message without needing a full error hierarchy.
    struct RegexProblem: LocalizedError {
        let message: String
        var errorDescription: String? { message }
    }

    struct RegexMatch: Identifiable {
        let id = UUID()
        let text: String
        let range: String
        let groups: [String]
    }

    static func testRegex(pattern: String, against subject: String,
                          caseInsensitive: Bool) -> Result<[RegexMatch], RegexProblem> {
        guard !pattern.isEmpty else { return .success([]) }
        var options: NSRegularExpression.Options = []
        if caseInsensitive { options.insert(.caseInsensitive) }
        do {
            let regex = try NSRegularExpression(pattern: pattern, options: options)
            let full = NSRange(subject.startIndex..., in: subject)
            let results = regex.matches(in: subject, range: full)
            return .success(results.map { match in
                let matched = (subject as NSString).substring(with: match.range)
                var groups: [String] = []
                for index in 1..<match.numberOfRanges {
                    let r = match.range(at: index)
                    groups.append(r.location == NSNotFound
                                  ? "" : (subject as NSString).substring(with: r))
                }
                return RegexMatch(
                    text: matched,
                    range: "\(match.range.location)–\(match.range.location + match.range.length)",
                    groups: groups)
            })
        } catch {
            return .failure(RegexProblem(message: error.localizedDescription))
        }
    }
}
