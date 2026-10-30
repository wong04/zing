import SwiftUI

struct NetworkView: View {
    @State private var resolver = DoHProfile.resolvers[0]
    @State private var profileName = ""
    @State private var generatedProfile: URL?
    @State private var errorText: String?
    @State private var wireGuardInstalled = ShellRunner.which("wg-quick") != nil

    var body: some View {
        PageContainer {
            dohSection
            vpnSection
            honestySection
        }
        .animation(Motion.standard, value: generatedProfile)
    }

    // MARK: - Encrypted DNS

    private var dohSection: some View {
        SectionCard(title: "Encrypted DNS for the whole Mac") {
            VStack(alignment: .leading, spacing: 12) {
                HStack(spacing: 10) {
                    PillMenu(options: DoHProfile.resolvers.map { ($0, $0.name) },
                             selection: $resolver)
                    Text(resolver.note)
                        .font(.app(12))
                        .foregroundStyle(Palette.textSecondary)
                        .lineLimit(2)
                    Spacer(minLength: 0)
                    InfoPopoverButton(text:
                        "Open a Link only encrypts lookups for pages it fetches itself. "
                        + "A profile switches the whole Mac — Safari, Chrome, everything — so "
                        + "DNS-level blocks stop applying anywhere.\n\nThe optional name is "
                        + "cosmetic: anyone opening the profile still sees a DNS setting "
                        + "pointing at \(resolver.name).")
                }

                PillField(placeholder: "Profile name (optional)", text: $profileName)

                HStack(spacing: 8) {
                    PillButton("Generate Profile", icon: "arrow.down.doc", style: .filled) {
                        generateProfile()
                    }
                    if let profile = generatedProfile {
                        PillButton("Show in Finder", style: .ghost) {
                            NSWorkspace.shared.activateFileViewerSelecting([profile])
                        }
                        .transition(.opacity)
                    }
                }

                if generatedProfile != nil {
                    Text("Saved. Open it, then approve it in **System Settings › General › "
                         + "Device Management**.")
                        .font(.app(12))
                        .foregroundStyle(Palette.textSecondary)
                        .transition(.opacity)
                }

                if let errorText {
                    InfoBanner(kind: .warning, text: errorText)
                }
            }
        }
    }

    // MARK: - VPN

    private var vpnSection: some View {
        SectionCard(title: "VPN") {
            VStack(alignment: .leading, spacing: 12) {
                HStack(spacing: 8) {
                    Text("Krackn can't be a VPN, but it can help you use one.")
                        .font(.app(13))
                        .foregroundStyle(Palette.text)
                    InfoPopoverButton(text:
                        "A VPN is a server somewhere else that forwards your traffic; an app "
                        + "is only the part that dials it. Two ways to get one:\n\n"
                        + "**A provider** — Mullvad, Proton and IVPN hand you a WireGuard "
                        + "config for a few dollars a month.\n\n"
                        + "**Your own server** — a small VPS running WireGuard. More setup, "
                        + "no subscription.\n\nBringing a tunnel up changes your routing "
                        + "table and needs your password, so you run it yourself.")
                }

                HStack(spacing: 7) {
                    Image(systemName: wireGuardInstalled ? "checkmark.circle" : "circle.dashed")
                        .foregroundStyle(wireGuardInstalled ? Palette.positive : Palette.textSecondary)
                    Text(wireGuardInstalled ? "wireguard-tools installed"
                                            : "wireguard-tools not installed")
                        .font(.app(12.5))
                        .foregroundStyle(Palette.textSecondary)
                }

                if !wireGuardInstalled {
                    codeLine("brew install wireguard-tools")
                }
                codeLine("sudo wg-quick up /path/to/your.conf")
                codeLine("sudo wg-quick down /path/to/your.conf")
            }
        }
    }

    private var honestySection: some View {
        SectionCard(title: "What beats what") {
            VStack(alignment: .leading, spacing: 9) {
                row("DNS-level block", "Encrypted DNS profile, or the app's DoH method", true)
                row("IP-level block", "Reader relay, or a VPN", false)
                row("TLS/SNI inspection", "Reader relay, or a VPN", false)
                row("Full proxy with TLS interception", "Only a VPN, if it isn't blocked too", false)

                Text("**Diagnose** tells you which one a site is.")
                .font(.app(11.5))
                .foregroundStyle(Palette.textSecondary)
                .padding(.top, 3)
            }
        }
    }

    private func row(_ blockType: String, _ fix: String, _ handledLocally: Bool) -> some View {
        HStack(alignment: .top, spacing: 8) {
            Image(systemName: handledLocally ? "checkmark.circle.fill" : "exclamationmark.circle")
                .foregroundStyle(handledLocally ? Palette.positive : Palette.caution)
            VStack(alignment: .leading, spacing: 1) {
                Text(blockType).font(.app(13, .medium))
                Text(fix).font(.app(11.5)).foregroundStyle(Palette.textSecondary)
            }
            Spacer()
        }
    }

    private func codeLine(_ command: String) -> some View {
        HStack {
            Text(command)
                .font(.system(size: 12, design: .monospaced))
                .textSelection(.enabled)
            Spacer()
            CopyButton(text: command)
                .controlSize(.small)
                .labelStyle(.iconOnly)
        }
        .padding(8)
        .wellSurface(radius: 9)
    }

    private func generateProfile() {
        let panel = NSSavePanel()
        panel.nameFieldStringValue = DoHProfile.fileName(
            for: resolver, displayName: profileName)
        panel.canCreateDirectories = true
        guard panel.runModal() == .OK, let url = panel.url else { return }

        do {
            try DoHProfile.makeProfile(for: resolver, displayName: profileName)
                .write(to: url, atomically: true, encoding: .utf8)
            generatedProfile = url
            errorText = nil
        } catch {
            errorText = error.localizedDescription
        }
    }
}
