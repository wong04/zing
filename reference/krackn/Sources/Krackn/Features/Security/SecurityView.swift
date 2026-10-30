import SwiftUI

struct SecurityView: View {
    private static let notAnAntivirus: String =
        "This is **not** a signature-scanning antivirus, and it doesn't try "
        + "to be one. Shipping a definitions database means updating it "
        + "continuously, which this app can't do — there's no server behind "
        + "it and no third-party dependencies. Your Mac already runs a "
        + "signature scanner: **XProtect**, with definitions Apple pushes "
        + "automatically. Duplicating that badly would be worse than not "
        + "duplicating it at all."
    private static let whatItDoes: String =
        "What it does instead is the part macOS leaves to you: checking "
        + "where a link actually goes *before* you follow it, verifying a "
        + "download is signed by who it claims and hasn't had its quarantine "
        + "flag stripped, catching the naming tricks that make a program look "
        + "like a document, and routing lookups through resolvers that refuse "
        + "known-malicious domains."

    @ObservedObject private var scanner = ThreatScanner.shared

    @AppStorage(AppSettings.Key.securityRealtime) private var realtime = true
    @AppStorage(AppSettings.Key.securityDownloadScan) private var downloadScan = true
    @AppStorage(AppSettings.Key.securityDomainBlocking) private var domainBlocking = true
    @AppStorage(AppSettings.Key.securitySignatureCheck) private var signatureCheck = true

    @State private var fileToScan: URL?
    @State private var fileVerdict: ThreatVerdict?
    @State private var fileChecksum: String?
    @State private var scanning = false

    var body: some View {
        PageContainer {
            scopeLine
            statusCard
            protectionCard
            scanFileCard
            if !scanner.history.isEmpty { activityCard }
        }
        .task { await scanner.refreshPosture() }
    }

    // MARK: - What this is

    /// One line on what this is — and, one click away, the full account of
    /// what it isn't. Overclaiming here would be worse than saying nothing.
    private var scopeLine: some View {
        HStack(spacing: 8) {
            Text("Link and download checks — not an antivirus.")
                .font(.app(13))
                .foregroundStyle(Palette.textSecondary)
            InfoPopoverButton(text: Self.notAnAntivirus + "\n\n" + Self.whatItDoes)
        }
    }

    // MARK: - System posture

    private var statusCard: some View {
        SectionCard(title: "This Mac") {
            VStack(alignment: .leading, spacing: 8) {
                if !scanner.posture.checked {
                    HStack(spacing: 8) {
                        ProgressView().controlSize(.small)
                        Text("Checking…").font(.app(12))
                            .foregroundStyle(Palette.textSecondary)
                    }
                } else {
                    postureRow("Gatekeeper", scanner.posture.gatekeeper,
                               on: "Enabled — unsigned apps are refused",
                               off: "Disabled — anything can run without a check")
                    postureRow("System Integrity Protection",
                               scanner.posture.systemIntegrityProtection,
                               on: "Enabled — system files are locked",
                               off: "Disabled — system files can be modified")
                    postureRow("FileVault", scanner.posture.fileVault,
                               on: "On — the disk is encrypted at rest",
                               off: "Off — anyone with the disk can read it")
                    HStack(spacing: 9) {
                        Image(systemName: scanner.posture.xprotectVersion != nil
                              ? "checkmark.circle.fill" : "questionmark.circle")
                            .foregroundStyle(scanner.posture.xprotectVersion != nil
                                             ? Palette.positive : Palette.textSecondary)
                        VStack(alignment: .leading, spacing: 1) {
                            Text("XProtect (Apple's malware scanner)")
                                .font(.app(12.5, .medium))
                                .foregroundStyle(Palette.text)
                            Text(scanner.posture.xprotectVersion.map {
                                "Definitions version \($0)"
                            } ?? "Version couldn't be read")
                                .font(.app(11))
                                .foregroundStyle(Palette.textSecondary)
                        }
                        Spacer()
                    }
                }
            }
        }
    }

    private func postureRow(_ title: String, _ value: Bool?,
                            on: String, off: String) -> some View {
        HStack(spacing: 9) {
            Image(systemName: value == nil ? "questionmark.circle"
                  : (value! ? "checkmark.circle.fill" : "exclamationmark.triangle.fill"))
                .foregroundStyle(value == nil ? Palette.textSecondary
                                 : (value! ? Palette.positive : Palette.caution))
            VStack(alignment: .leading, spacing: 1) {
                Text(title)
                    .font(.app(12.5, .medium))
                    .foregroundStyle(Palette.text)
                Text(value == nil ? "Couldn't be determined" : (value! ? on : off))
                    .font(.app(11))
                    .foregroundStyle(Palette.textSecondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
            Spacer(minLength: 0)
        }
    }

    // MARK: - Toggles

    private var protectionCard: some View {
        SectionCard(title: "Protection") {
            VStack(alignment: .leading, spacing: 13) {
                toggle($realtime, "Check links before loading",
                       "Look-alike characters, brand impersonation, hidden destinations.")
                toggle($domainBlocking, "Block known-malicious domains",
                       "Refused only when Quad9 and Cloudflare both list it.")
                toggle($downloadScan, "Scan downloads",
                       "Does a file's content match its name?")
                toggle($signatureCheck, "Verify signatures on programs",
                       "Gatekeeper's check, plus stripped quarantine flags.")

                if !realtime && !downloadScan {
                    InfoBanner(kind: .warning,
                               text: "With both of these off, nothing here is protecting you.")
                }

                HStack(spacing: 14) {
                    stat("\(scanner.scannedCount)", "checked")
                    stat("\(scanner.blockedCount)", "blocked")
                    Spacer()
                }
                .padding(.top, 2)
            }
        }
    }

    private func toggle(_ binding: Binding<Bool>, _ title: String,
                        _ detail: String) -> some View {
        VStack(alignment: .leading, spacing: 3) {
            Toggle(title, isOn: binding).toggleStyle(.pill)
            Text(detail)
                .font(.app(11))
                .foregroundStyle(Palette.textSecondary)
                .fixedSize(horizontal: false, vertical: true)
        }
    }

    private func stat(_ value: String, _ label: String) -> some View {
        HStack(spacing: 5) {
            Text(value)
                .font(.app(19, .semibold)).monospacedDigit()
                .foregroundStyle(Palette.text)
            Text(label)
                .font(.app(11))
                .foregroundStyle(Palette.textSecondary)
        }
    }

    // MARK: - Manual scan

    private var scanFileCard: some View {
        SectionCard(title: "Check a file") {
            VStack(alignment: .leading, spacing: 12) {
                dropZone

                if scanning {
                    HStack(spacing: 8) {
                        ProgressView().controlSize(.small)
                        Text("Inspecting…").font(.app(12))
                            .foregroundStyle(Palette.textSecondary)
                    }
                }

                if let file = fileToScan, let verdict = fileVerdict {
                    VerdictCard(subject: file, verdict: verdict, checksum: fileChecksum)
                }
            }
        }
    }

    @State private var isTargeted = false

    private var dropZone: some View {
        VStack(spacing: 9) {
            Image(systemName: "shield.lefthalf.filled")
                .font(.system(size: 24, weight: .light))
                .foregroundStyle(isTargeted ? Palette.accentText : Palette.textSecondary)
            Text("Drop a file to check it")
                .font(.app(12.5))
                .foregroundStyle(Palette.textSecondary)
            PillButton("Choose File", icon: "folder", style: .outlined, size: .small) {
                let panel = NSOpenPanel()
                panel.allowsMultipleSelection = false
                if panel.runModal() == .OK, let url = panel.url { scan(url) }
            }
        }
        .frame(maxWidth: .infinity)
        .padding(.vertical, 22)
        .background(RoundedRectangle(cornerRadius: Metrics.cardRadius, style: .continuous)
            .fill(isTargeted ? Palette.accent.opacity(0.08) : Palette.controlFill.opacity(0.5)))
        .background(RoundedRectangle(cornerRadius: Metrics.cardRadius, style: .continuous)
            .strokeBorder(isTargeted ? Palette.accent : Palette.separator,
                          style: StrokeStyle(lineWidth: 1, dash: [5, 4])))
        .animation(Motion.hover, value: isTargeted)
        .dropsFiles(isTargeted: $isTargeted) { urls in
            guard let first = urls.first else { return }
            scan(first)
        }
    }

    private func scan(_ url: URL) {
        fileToScan = url
        fileVerdict = nil
        fileChecksum = nil
        scanning = true
        Task {
            let verdict = await scanner.scanDownload(at: url)
            // The checksum is what you compare against a project's published
            // hash, which is the only way to know a download is the file the
            // author actually released.
            let digest = try? ThreatScanner.checksum(of: url)
            await MainActor.run {
                fileVerdict = verdict
                fileChecksum = digest
                scanning = false
            }
        }
    }

    // MARK: - Activity

    private var activityCard: some View {
        SectionCard(title: "Recent checks") {
            VStack(alignment: .leading, spacing: 7) {
                ForEach(scanner.history.prefix(12)) { record in
                    HStack(spacing: 9) {
                        Image(systemName: record.verdict.level.icon)
                            .font(.app(12))
                            .foregroundStyle(colour(for: record.verdict.level))
                        VStack(alignment: .leading, spacing: 1) {
                            Text(record.subject)
                                .font(.app(12, .medium))
                                .foregroundStyle(Palette.text)
                                .lineLimit(1)
                                .truncationMode(.middle)
                            Text(record.verdict.summary)
                                .font(.app(10.5))
                                .foregroundStyle(Palette.textSecondary)
                                .lineLimit(1)
                        }
                        Spacer(minLength: 6)
                        Text(record.when.formatted(date: .omitted, time: .shortened))
                            .font(.app(10))
                            .foregroundStyle(Palette.textSecondary.opacity(0.8))
                    }
                    .padding(.vertical, 3)
                }
                PillButton("Clear", style: .ghost, size: .small) { scanner.clearHistory() }
                    .padding(.top, 4)
            }
        }
    }

    private func colour(for level: ThreatLevel) -> Color {
        switch level {
        case .clean: return Palette.positive
        case .notice: return Palette.textSecondary
        case .suspicious: return Palette.caution
        case .dangerous: return Palette.danger
        }
    }
}

/// The result of inspecting one file, spelled out finding by finding.
private struct VerdictCard: View {
    let subject: URL
    let verdict: ThreatVerdict
    let checksum: String?

    private var tone: Color {
        switch verdict.level {
        case .clean: return Palette.positive
        case .notice: return Palette.accent
        case .suspicious: return Palette.caution
        case .dangerous: return Palette.danger
        }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 11) {
            HStack(spacing: 10) {
                FileIcon(url: subject, size: 26)
                VStack(alignment: .leading, spacing: 1) {
                    Text(subject.lastPathComponent)
                        .font(.app(13, .semibold))
                        .foregroundStyle(Palette.text)
                        .lineLimit(1)
                        .truncationMode(.middle)
                    Text(verdict.level.label)
                        .font(.app(11, .semibold))
                        .foregroundStyle(tone)
                }
                Spacer()
                Image(systemName: verdict.level.icon)
                    .font(.app(20))
                    .foregroundStyle(tone)
            }

            if verdict.findings.isEmpty {
                Text("Nothing suspicious about this file's name, type or signature.")
                    .font(.app(12))
                    .foregroundStyle(Palette.textSecondary)
            } else {
                ForEach(verdict.findings) { finding in
                    VStack(alignment: .leading, spacing: 2) {
                        Text(finding.title)
                            .font(.app(12, .semibold))
                            .foregroundStyle(Palette.text)
                        Text(finding.detail)
                            .font(.app(11))
                            .foregroundStyle(Palette.textSecondary)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                }
            }

            if let checksum {
                VStack(alignment: .leading, spacing: 3) {
                    Text("SHA-256")
                        .font(.app(10, .semibold))
                        .foregroundStyle(Palette.textSecondary)
                    HStack {
                        Text(checksum)
                            .font(.system(size: 10, design: .monospaced))
                            .textSelection(.enabled)
                            .lineLimit(1)
                            .truncationMode(.middle)
                        Spacer()
                        CopyButton(text: checksum).controlSize(.small).labelStyle(.iconOnly)
                    }
                    Text("Compare this with the checksum published by whoever made the "
                         + "file. If they don't match, it isn't the file they released.")
                        .font(.app(10))
                        .foregroundStyle(Palette.textSecondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
                .padding(9)
                .wellSurface(radius: 9)
            }
        }
        .padding(13)
        .background(tone.opacity(0.09), in: RoundedRectangle(cornerRadius: 12))
        .overlay(RoundedRectangle(cornerRadius: 12)
            .strokeBorder(tone.opacity(0.35), lineWidth: 1))
    }
}
