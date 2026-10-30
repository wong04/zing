import Foundation

/// Generates a macOS configuration profile that points the whole system at a
/// DNS-over-HTTPS resolver.
///
/// This matters because the in-app DoH only helps pages fetched through this
/// app. A `.mobileconfig` installed in System Settings changes resolution for
/// Safari, Chrome, and everything else — it's the supported OS mechanism, not
/// a hack, and it survives reboots.
enum DoHProfile {

    struct Resolver: Identifiable, Hashable {
        let name: String
        let url: String
        let addresses: [String]
        let note: String

        var id: String { name }
    }

    static let resolvers: [Resolver] = [
        .init(
            name: "Cloudflare",
            url: "https://cloudflare-dns.com/dns-query",
            addresses: ["1.1.1.1", "1.0.0.1"],
            note: "Fastest in most tests. No filtering."
        ),
        .init(
            name: "Cloudflare (malware blocking)",
            url: "https://security.cloudflare-dns.com/dns-query",
            addresses: ["1.1.1.2", "1.0.0.2"],
            note: "Same speed, but blocks known-malicious domains."
        ),
        .init(
            name: "Google",
            url: "https://dns.google/dns-query",
            addresses: ["8.8.8.8", "8.8.4.4"],
            note: "Very reliable, widely reachable."
        ),
        .init(
            name: "Quad9",
            url: "https://dns.quad9.net/dns-query",
            addresses: ["9.9.9.9", "149.112.112.112"],
            note: "Privacy-focused, blocks malicious domains."
        ),
    ]

    /// Builds the profile XML. The payload UUIDs are regenerated each time so
    /// installing a second profile doesn't silently replace the first.
    ///
    /// `PayloadScope` must be `System`, and its absence is not cosmetic. A DNS
    /// settings payload is backed by a NetworkExtension service that applies to
    /// the whole machine, and that service cannot be created inside a
    /// user-scoped profile — which is what a profile defaults to. Omitting the
    /// key makes macOS refuse the install with a thoroughly misleading message:
    ///
    ///     The 'VPN Service' payload could not be installed.
    ///     The VPN service could not be created.
    ///
    /// There is no VPN payload in this profile and never has been; that wording
    /// is just how the installer reports a failure in the subsystem that also
    /// backs VPNs. Don't go looking for a VPN payload to remove.
    ///
    /// `displayName` is the label System Settings shows. It's cosmetic — the
    /// payload type and resolver URL below are always visible to anyone who
    /// opens the file or clicks into the profile.
    static func makeProfile(for resolver: Resolver, displayName: String? = nil) -> String {
        let payloadUUID = UUID().uuidString
        let profileUUID = UUID().uuidString
        let addressEntries = resolver.addresses
            .map { "                    <string>\($0)</string>" }
            .joined(separator: "\n")

        let title = displayName?.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty == false
            ? escape(displayName!)
            : "Encrypted DNS — \(escape(resolver.name))"

        return """
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" \
        "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
            <key>PayloadContent</key>
            <array>
                <dict>
                    <key>DNSSettings</key>
                    <dict>
                        <key>DNSProtocol</key>
                        <string>HTTPS</string>
                        <key>ServerURL</key>
                        <string>\(resolver.url)</string>
                        <key>ServerAddresses</key>
                        <array>
        \(addressEntries)
                        </array>
                    </dict>
                    <key>PayloadDescription</key>
                    <string>Routes all DNS queries through \(escape(resolver.name)) over HTTPS.</string>
                    <key>PayloadDisplayName</key>
                    <string>DNS over HTTPS — \(escape(resolver.name))</string>
                    <key>PayloadIdentifier</key>
                    <string>com.krackn.doh.\(payloadUUID)</string>
                    <key>PayloadType</key>
                    <string>com.apple.dnsSettings.managed</string>
                    <key>PayloadUUID</key>
                    <string>\(payloadUUID)</string>
                    <key>PayloadVersion</key>
                    <integer>1</integer>
                    <key>ProhibitDisablement</key>
                    <false/>
                </dict>
            </array>
            <key>PayloadDisplayName</key>
            <string>\(title)</string>
            <key>PayloadDescription</key>
            <string>Sets this Mac to resolve DNS through \(escape(resolver.name)) over HTTPS. \
        Remove it any time from System Settings › General › Device Management.</string>
            <key>PayloadIdentifier</key>
            <string>com.krackn.profile.\(profileUUID)</string>
            <key>PayloadOrganization</key>
            <string>\(title)</string>
            <key>PayloadRemovalDisallowed</key>
            <false/>
            <key>PayloadType</key>
            <string>Configuration</string>
            <key>PayloadUUID</key>
            <string>\(profileUUID)</string>
            <key>PayloadVersion</key>
            <integer>1</integer>
            <key>PayloadScope</key>
            <string>System</string>
        </dict>
        </plist>
        """
    }

    /// A name typed by the user can contain characters that are markup in XML.
    /// Without escaping, an ampersand alone produces a profile macOS refuses
    /// to parse.
    private static func escape(_ text: String) -> String {
        text
            .replacingOccurrences(of: "&", with: "&amp;")
            .replacingOccurrences(of: "<", with: "&lt;")
            .replacingOccurrences(of: ">", with: "&gt;")
            .replacingOccurrences(of: "\"", with: "&quot;")
            .replacingOccurrences(of: "'", with: "&apos;")
    }

    /// Turns a display name into something safe to use as a filename.
    static func fileName(for resolver: Resolver, displayName: String?) -> String {
        let base = displayName?.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty == false
            ? displayName!
            : "DoH-\(resolver.name)"
        let cleaned = base
            .components(separatedBy: CharacterSet(charactersIn: "/\\:*?\"<>|"))
            .joined()
            .replacingOccurrences(of: " ", with: "-")
        return "\(cleaned).mobileconfig"
    }

    /// Writes the profile and reveals it in Finder. Installation itself is a
    /// System Settings action requiring the user's admin password — we can't
    /// and shouldn't do that part for them.
    static func write(
        _ resolver: Resolver, to directory: URL, displayName: String? = nil
    ) throws -> URL {
        let destination = directory
            .appendingPathComponent(fileName(for: resolver, displayName: displayName))
        try makeProfile(for: resolver, displayName: displayName)
            .write(to: destination, atomically: true, encoding: .utf8)
        return destination
    }
}
