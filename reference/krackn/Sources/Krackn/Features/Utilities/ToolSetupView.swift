import SwiftUI

/// One-click setup for the optional engines. Nothing here asks the user to
/// open a Terminal or type a `brew` command — the app fetches static binaries
/// into its own folder, so no admin password is needed either.
struct ToolSetupView: View {
    @ObservedObject private var installer = ToolInstaller.shared
    /// Off inside Settings, where the category is already called "Engines".
    var showsTitle = true

    private var missingCount: Int {
        ManagedTool.all.filter { !installer.isInstalled($0) }.count
    }

    var body: some View {
        SectionCard(title: showsTitle ? "Conversion engines" : "") {
            VStack(alignment: .leading, spacing: 14) {
                Text("Adds audio, video, RAW, publishing and 3D formats. Installed into "
                     + "Krackn's own folder — no Terminal, no password.")
                .font(.app(12))
                .foregroundStyle(Palette.textSecondary)
                .fixedSize(horizontal: false, vertical: true)

                if missingCount > 0 {
                    PillButton("Set Up All Tools", icon: "arrow.down.circle", style: .filled) {
                        Task { await installer.installAllMissing() }
                    }
                } else {
                    HStack(spacing: 7) {
                        Image(systemName: "checkmark.circle.fill")
                            .foregroundStyle(Palette.positive)
                        Text("Everything is set up.")
                            .font(.app(13, .medium))
                            .foregroundStyle(Palette.text)
                    }
                }

                VStack(spacing: 8) {
                    ForEach(ManagedTool.all) { tool in
                        ToolRow(tool: tool, installer: installer)
                    }
                }
            }
        }
        .task { installer.refreshIfNeeded() }
    }
}

private struct ToolRow: View {
    let tool: ManagedTool
    @ObservedObject var installer: ToolInstaller

    var body: some View {
        HStack(spacing: 11) {
            statusIcon
                .frame(width: 20)

            VStack(alignment: .leading, spacing: 1) {
                Text(tool.title)
                    .font(.app(13, .semibold))
                    .foregroundStyle(Palette.text)
                Text(detail)
                    .font(.app(11))
                    .foregroundStyle(Palette.textSecondary)
                    .lineLimit(2)
                    .fixedSize(horizontal: false, vertical: true)
            }

            Spacer(minLength: 8)

            switch installer.state(of: tool) {
            case .installing:
                ProgressView().controlSize(.small)
            case .installed:
                Text("Ready")
                    .font(.app(11, .semibold))
                    .foregroundStyle(Palette.positive)
            case .notInstalled, .failed:
                PillButton("Install", style: .outlined, size: .small) {
                    Task { await installer.install(tool) }
                }
            }
        }
        .padding(11)
        .background(Palette.well.opacity(0.5), in: RoundedRectangle(cornerRadius: 11))
        .overlay(
            RoundedRectangle(cornerRadius: 11)
                .strokeBorder(Palette.separator, lineWidth: 1)
        )
    }

    @ViewBuilder
    private var statusIcon: some View {
        switch installer.state(of: tool) {
        case .installed:
            Image(systemName: "checkmark.circle.fill").foregroundStyle(Palette.positive)
        case .failed:
            Image(systemName: "exclamationmark.triangle.fill").foregroundStyle(Palette.caution)
        case .installing:
            Image(systemName: "arrow.down.circle").foregroundStyle(Palette.accent)
        case .notInstalled:
            Image(systemName: "circle.dashed").foregroundStyle(Palette.textSecondary)
        }
    }

    private var detail: String {
        switch installer.state(of: tool) {
        case .installing(let note): return note
        case .failed(let why): return why
        default: return tool.purpose
        }
    }
}
