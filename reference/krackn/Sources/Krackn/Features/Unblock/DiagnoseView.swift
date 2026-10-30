import SwiftUI

@MainActor
final class DiagnoseModel: ObservableObject {
    @Published var input = ""
    @Published var report: DiagnosticReport?
    @Published var status = ""
    @Published var isWorking = false
    @Published var errorText: String?

    func run() async {
        guard let url = try? RelayEngine.normalize(input), let host = url.host else {
            errorText = "That doesn't look like a URL or hostname."
            return
        }
        isWorking = true
        errorText = nil
        report = nil
        defer { isWorking = false }

        report = await BlockDiagnostics.diagnose(host: host) { [weak self] note in
            Task { @MainActor in self?.status = note }
        }
        status = ""
    }
}

struct DiagnoseView: View {
    @StateObject private var model = DiagnoseModel()

    var body: some View {
        PageContainer {
            CommandBar(placeholder: "Site to test, e.g. example.com", text: $model.input,
                       icon: "magnifyingglass", action: "Test",
                       isWorking: model.isWorking,
                       onRun: { Task { await model.run() } })

            if model.isWorking && !model.status.isEmpty {
                Text(model.status)
                    .font(.app(12.5))
                    .foregroundStyle(Palette.textSecondary)
                    .transition(.opacity)
            }

            if let error = model.errorText {
                InfoBanner(kind: .warning, text: error)
            }

            if let report = model.report {
                verdict(report)
                evidence(report)
                recommendation(report)
            }
        }
        .animation(Motion.standard, value: model.report?.kind.title)
    }

    private func verdict(_ report: DiagnosticReport) -> some View {
        let kind: InfoBanner.Kind = {
            switch report.kind {
            case .notBlocked: return .success
            case .siteDown, .inconclusive: return .info
            default: return .warning
            }
        }()

        return VStack(alignment: .leading, spacing: 8) {
            Text(report.kind.title).font(.app(18, .semibold))
            InfoBanner(kind: kind, text: report.kind.detail)
        }
    }

    private func evidence(_ report: DiagnosticReport) -> some View {
        SectionCard(title: "Findings") {
            VStack(alignment: .leading, spacing: 6) {
                ForEach(report.steps, id: \.self) { step in
                    HStack(alignment: .top, spacing: 7) {
                        Text("•").foregroundStyle(Palette.textSecondary)
                        Text(step)
                            .font(.system(size: 12, design: .monospaced))
                            .textSelection(.enabled)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                }
            }
        }
    }

    private func recommendation(_ report: DiagnosticReport) -> some View {
        SectionCard(title: "What to try") {
            VStack(alignment: .leading, spacing: 8) {
                ForEach(report.kind.recommended) { strategy in
                    HStack(alignment: .top, spacing: 7) {
                        Image(systemName: "arrow.right.circle.fill")
                            .foregroundStyle(Palette.accent)
                        VStack(alignment: .leading, spacing: 2) {
                            Text(strategy.rawValue).font(.app(13, .medium))
                            Text(strategy.explanation)
                                .font(.app(11.5))
                                .foregroundStyle(Palette.textSecondary)
                                .fixedSize(horizontal: false, vertical: true)
                        }
                    }
                }
            }
        }
    }
}
