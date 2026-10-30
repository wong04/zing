import Foundation
import CryptoKit
import SwiftUI

/// A completed scan, kept so the security page can show what it has been doing
/// rather than being a wall of toggles with no evidence behind them.
struct ScanRecord: Identifiable {
    let id = UUID()
    let when: Date
    let subject: String
    let kind: Kind
    let verdict: ThreatVerdict

    enum Kind: String { case download = "Download", navigation = "Site" }
}

/// How the Mac's own defences are configured. Krackn doesn't replace any of
/// these — surfacing them honestly is more useful than pretending to be the
/// thing already protecting the machine.
struct SystemPosture {
    var gatekeeper: Bool?
    var systemIntegrityProtection: Bool?
    var fileVault: Bool?
    var xprotectVersion: String?
    var checked = false
}

/// The security engine.
///
/// A word on what this is and isn't. It is not a signature-scanning antivirus:
/// building one means shipping and continuously updating a definitions
/// database, which this app can't do with no third-party dependencies and no
/// server behind it. macOS already runs one — XProtect — with definitions Apple
/// pushes automatically, and duplicating it badly would be worse than not
/// duplicating it.
///
/// What this does instead is the part macOS *doesn't* do for you: check where a
/// link actually goes before you follow it, verify that a download is signed by
/// who it claims and hasn't had its quarantine flag stripped, catch the naming
/// tricks used to make a program look like a document, and route DNS through a
/// resolver that refuses known-malicious domains outright.
@MainActor
final class ThreatScanner: ObservableObject {
    static let shared = ThreatScanner()

    @Published private(set) var history: [ScanRecord] = []
    @Published private(set) var posture = SystemPosture()
    @Published private(set) var blockedCount = 0
    @Published private(set) var scannedCount = 0
    /// Hosts the user chose to visit anyway, so a warning isn't re-shown for
    /// the rest of the session.
    private var allowedHosts: Set<String> = []

    private init() {}

    // MARK: - Toggles

    // All four default to on when unset. Protection that quietly defaults to
    // off because a key hasn't been written yet is worse than no protection,
    // because the UI still says it's enabled.
    var realtimeEnabled: Bool { AppSettings.securityFlag(.securityRealtime) }
    var downloadScanEnabled: Bool { AppSettings.securityFlag(.securityDownloadScan) }
    var domainBlockingEnabled: Bool { AppSettings.securityFlag(.securityDomainBlocking) }
    var signatureCheckEnabled: Bool { AppSettings.securityFlag(.securitySignatureCheck) }

    // MARK: - Navigation

    /// Vets a URL before the browser requests it. Returns nil to allow.
    ///
    /// Synchronous on purpose: this sits in `decidePolicyFor`, where WebKit is
    /// waiting on the answer, so it can only do pattern work. The slow,
    /// network-dependent half — asking a filtering resolver whether the host is
    /// a known-bad one — runs separately and populates `blockedDomains`.
    func checkNavigation(_ url: URL) -> ThreatVerdict? {
        guard realtimeEnabled else { return nil }
        guard let scheme = url.scheme?.lowercased(),
              scheme == "http" || scheme == "https" else { return nil }

        let host = (url.host ?? "").lowercased()
        guard !allowedHosts.contains(host) else { return nil }

        var verdict = ThreatHeuristics.inspect(url: url)

        if domainBlockingEnabled, blockedDomains.contains(host) {
            verdict = ThreatVerdict(subject: verdict.subject, findings: verdict.findings + [
                ThreatFinding(
                    level: .dangerous,
                    title: "Known-malicious domain",
                    detail: "\(host) is refused by the filtering DNS resolver, which "
                        + "means it appears on a current malware or phishing list.")
            ])
        }

        guard verdict.level >= .suspicious else { return nil }

        scannedCount += 1
        if verdict.isBlocking { blockedCount += 1 }
        record(ScanRecord(when: Date(), subject: host, kind: .navigation, verdict: verdict))
        return verdict
    }

    /// Lets the user override a block for the rest of the session.
    func allowHost(_ host: String) {
        allowedHosts.insert(host.lowercased())
    }

    // MARK: - Domain reputation

    /// Hosts a filtering resolver refused. Populated lazily: the first time a
    /// host is seen it's queued for a check, so the answer is ready by the
    /// next visit rather than blocking this one.
    private var blockedDomains: Set<String> = []
    private var checkedDomains: Set<String> = []

    /// Asks a malware-filtering resolver about a host. These resolvers answer
    /// `0.0.0.0`, `NXDOMAIN`, or a sinkhole for domains on their block lists —
    /// so a disagreement with an unfiltered resolver *is* the reputation
    /// signal, no separate feed required.
    func vetDomainInBackground(_ host: String) {
        let lowered = host.lowercased()
        guard domainBlockingEnabled, !lowered.isEmpty,
              !checkedDomains.contains(lowered) else { return }
        checkedDomains.insert(lowered)

        Task { [weak self] in
            let flagged = await Self.isRefusedByFilteringResolver(lowered)
            guard flagged else { return }
            await MainActor.run { self?.blockedDomains.insert(lowered) }
        }
    }

    nonisolated static func isRefusedByFilteringResolver(_ host: String) async -> Bool {
        // Quad9 and Cloudflare's security resolver both block known-malicious
        // names. Requiring agreement between two independent lists keeps a
        // single provider's false positive from blocking a legitimate site.
        async let quad9 = refuses(host, using: .quad9)
        async let cloudflare = refuses(host, using: .cloudflareSecurity)
        let (a, b) = await (quad9, cloudflare)
        return a && b
    }

    private nonisolated static func refuses(
        _ host: String, using provider: DoHResolver.Provider
    ) async -> Bool {
        do {
            let addresses = try await DoHResolver.resolve(host, using: provider, timeout: 4)
            // An empty answer or an all-zero sinkhole is a refusal.
            if addresses.isEmpty { return true }
            return addresses.allSatisfy { $0 == "0.0.0.0" || $0 == "::" }
        } catch RelayError.nxdomain {
            return true
        } catch {
            // Network trouble is not evidence of malice.
            return false
        }
    }

    // MARK: - Downloads

    /// Inspects a finished download: what it's named, what it actually is,
    /// whether macOS quarantined it, and whether its signature checks out.
    func scanDownload(at url: URL) async -> ThreatVerdict {
        guard downloadScanEnabled else { return .clean }

        var findings = ThreatHeuristics.inspect(filename: url.lastPathComponent)

        // Sniff the real type from the first bytes and compare with the name.
        if let handle = try? FileHandle(forReadingFrom: url) {
            defer { try? handle.close() }
            let head = (try? handle.read(upToCount: 16)) ?? Data()
            findings += ThreatHeuristics.inspectContent(
                extension: url.pathExtension,
                sniffed: Self.sniffType(head))
        }

        let ext = url.pathExtension.lowercased()
        let isExecutable = ThreatHeuristics.executableExtensions.contains(ext)

        if isExecutable, signatureCheckEnabled {
            findings += await Self.verifySignature(of: url)
            findings += await Self.checkQuarantine(of: url)
        }

        let verdict = ThreatVerdict(subject: url.lastPathComponent, findings: findings)
        scannedCount += 1
        if verdict.isBlocking { blockedCount += 1 }
        record(ScanRecord(when: Date(), subject: url.lastPathComponent,
                          kind: .download, verdict: verdict))
        return verdict
    }

    /// Magic-number sniffing for the types that actually turn up in downloads.
    nonisolated static func sniffType(_ data: Data) -> String? {
        let b = [UInt8](data.prefix(16))
        guard b.count >= 4 else { return nil }
        if b.starts(with: [0x89, 0x50, 0x4E, 0x47]) { return "png" }
        if b.starts(with: [0xFF, 0xD8, 0xFF]) { return "jpg" }
        if b.starts(with: [0x47, 0x49, 0x46, 0x38]) { return "gif" }
        if b.starts(with: [0x25, 0x50, 0x44, 0x46]) { return "pdf" }
        if b.starts(with: [0x50, 0x4B, 0x03, 0x04]) { return "zip" }
        if b.starts(with: [0x1F, 0x8B]) { return "gz" }
        if b.starts(with: [0x37, 0x7A, 0xBC, 0xAF]) { return "7z" }
        if b.starts(with: [0x52, 0x61, 0x72, 0x21]) { return "rar" }
        // Mach-O, in the four shapes macOS emits.
        if b.starts(with: [0xCF, 0xFA, 0xED, 0xFE]) || b.starts(with: [0xCE, 0xFA, 0xED, 0xFE])
            || b.starts(with: [0xCA, 0xFE, 0xBA, 0xBE]) || b.starts(with: [0xBE, 0xBA, 0xFE, 0xCA]) {
            return "macho"
        }
        if b.starts(with: [0x4D, 0x5A]) { return "exe" }
        if b.starts(with: [0x23, 0x21]) { return "sh" }   // #!
        return nil
    }

    /// Runs the same check Gatekeeper runs when you double-click something.
    private nonisolated static func verifySignature(of url: URL) async -> [ThreatFinding] {
        guard let result = try? await ShellRunner.run(
            "/usr/sbin/spctl",
            ["-a", "-vv", "-t", "open", "--context", "context:primary-signature", url.path])
        else { return [] }

        let output = result.stderr + result.stdout
        if result.ok {
            if output.contains("Notarized Developer ID") {
                return [ThreatFinding(
                    level: .clean,
                    title: "Notarized by Apple",
                    detail: "Signed with a Developer ID and checked by Apple for known "
                        + "malware. This is the strongest assurance macOS offers.")]
            }
            return [ThreatFinding(
                level: .notice,
                title: "Accepted by Gatekeeper",
                detail: "Signed and allowed to run, though not notarized.")]
        }

        if output.contains("rejected") || output.contains("not signed") {
            return [ThreatFinding(
                level: .dangerous,
                title: "Rejected by Gatekeeper",
                detail: "This program is unsigned or its signature doesn't verify, so "
                    + "there's no way to tell who made it or whether it's been "
                    + "tampered with since. macOS will refuse to open it normally.")]
        }
        return []
    }

    /// A downloaded executable *should* be quarantined. If the flag is missing,
    /// something removed it — which is exactly what an installer script does to
    /// skip the Gatekeeper prompt.
    private nonisolated static func checkQuarantine(of url: URL) async -> [ThreatFinding] {
        guard let result = try? await ShellRunner.run(
            "/usr/bin/xattr", ["-p", "com.apple.quarantine", url.path])
        else { return [] }
        guard !result.ok else { return [] }
        return [ThreatFinding(
            level: .suspicious,
            title: "Quarantine flag is missing",
            detail: "Downloads normally carry a quarantine marker so macOS checks them "
                + "on first launch. This one doesn't, which means something stripped "
                + "it — that's how a Gatekeeper prompt gets skipped.")]
    }

    /// SHA-256 of a file, for checking a download against a published checksum.
    nonisolated static func checksum(of url: URL) throws -> String {
        let data = try Data(contentsOf: url, options: .mappedIfSafe)
        return SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined()
    }

    // MARK: - System posture

    func refreshPosture() async {
        var next = SystemPosture()

        if let gk = try? await ShellRunner.run("/usr/sbin/spctl", ["--status"]) {
            next.gatekeeper = (gk.stdout + gk.stderr).contains("assessments enabled")
        }
        if let sip = try? await ShellRunner.run("/usr/bin/csrutil", ["status"]) {
            next.systemIntegrityProtection = (sip.stdout + sip.stderr).contains("enabled")
        }
        if let fv = try? await ShellRunner.run("/usr/bin/fdesetup", ["status"]) {
            next.fileVault = (fv.stdout + fv.stderr).contains("FileVault is On")
        }
        next.xprotectVersion = Self.readXProtectVersion()
        next.checked = true
        posture = next
    }

    /// XProtect is Apple's own malware scanner. Its version is the definition
    /// date, which is what tells you whether the machine is current.
    private nonisolated static func readXProtectVersion() -> String? {
        let candidates = [
            "/Library/Apple/System/Library/CoreServices/XProtect.bundle/Contents/Info.plist",
            "/System/Library/CoreServices/XProtect.bundle/Contents/Info.plist",
        ]
        for path in candidates {
            guard let data = FileManager.default.contents(atPath: path),
                  let plist = try? PropertyListSerialization.propertyList(
                      from: data, format: nil) as? [String: Any]
            else { continue }
            if let version = plist["CFBundleShortVersionString"] as? String {
                return version
            }
        }
        return nil
    }

    // MARK: - History

    private func record(_ entry: ScanRecord) {
        history.insert(entry, at: 0)
        history = Array(history.prefix(50))
    }

    func clearHistory() { history = [] }
}
