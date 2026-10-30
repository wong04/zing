import SwiftUI
import WebKit

@MainActor
final class UnblockModel: ObservableObject {
    @Published var input = ""
    @Published var status = ""
    @Published var result: RelayResult?
    @Published var errorText: String?
    @Published var isWorking = false
    @Published var enabled: Set<RelayStrategy> = Set(RelayStrategy.allCases)
    @Published var history: [String] = []

    private let engine = RelayEngine()
    private var currentFetch: Task<Void, Never>?

    /// Stops an in-flight fetch. The engine checks for cancellation between
    /// strategies and the HTTP client tears down its connection, so this takes
    /// effect immediately rather than at the next timeout.
    func cancel() {
        currentFetch?.cancel()
        currentFetch = nil
        isWorking = false
        status = "Cancelled."
    }

    /// Runs `open()` as a cancellable task and tracks it.
    func start() {
        currentFetch?.cancel()
        currentFetch = Task { [weak self] in
            await self?.open()
        }
    }

    func open() async {
        guard !input.trimmingCharacters(in: .whitespaces).isEmpty else { return }
        isWorking = true
        errorText = nil
        result = nil
        status = "Starting…"
        defer { isWorking = false }

        do {
            let fetched = try await engine.fetch(
                urlString: input,
                enabled: enabled,
                progress: { [weak self] note in
                    Task { @MainActor in self?.status = note }
                }
            )
            result = fetched
            status = "Loaded via \(fetched.strategy.rawValue)"
            let cleaned = fetched.finalURL.absoluteString
            history.removeAll { $0 == cleaned }
            history.insert(cleaned, at: 0)
            history = Array(history.prefix(12))
        } catch is CancellationError {
            status = "Cancelled."
        } catch {
            errorText = error.localizedDescription
            status = ""
        }
    }
}

struct UnblockView: View {
    @StateObject private var model = UnblockModel()
    @State private var showStrategyOptions = false
    @State private var appLikeFailure = false

    /// Every load starts a fresh verdict — otherwise one app-like site leaves
    /// the warning stuck on for the next page.
    private func load() {
        appLikeFailure = false
        model.start()
    }

    /// Sites like Instagram are applications, not documents. They need browser
    /// APIs that only work on a real http(s) origin, which a custom scheme
    /// can't be — so the viewer can't run them no matter how the bytes arrive.
    private var appLikeNotice: some View {
        InfoBanner(
            kind: .warning,
            text: "**This site is an app, not a page,** and can't run in this viewer. "
                + "**Network & VPN → Generate Profile** lets it work normally in any browser."
        )
        .padding(.horizontal, 16)
        .padding(.top, 10)
    }

    /// Before anything loads, this is laid out exactly like Download and
    /// Diagnose — the same centred column, the same command bar. Once a page
    /// arrives it needs the whole width, so the bar moves into a slim strip
    /// along the top and the page fills the rest.
    var body: some View {
        Group {
            if model.result == nil {
                PageContainer {
                    bar
                    if model.isWorking || !model.status.isEmpty {
                        Text(model.status)
                            .font(.app(12.5))
                            .foregroundStyle(Palette.textSecondary)
                            .transition(.opacity)
                    }
                    if let error = model.errorText {
                        InfoBanner(kind: .warning, text: error)
                        Text("If every method failed, the block is at the IP or TLS layer. "
                             + "**Diagnose** confirms it; **Network & VPN** is the way past it.")
                            .font(.app(12))
                            .foregroundStyle(Palette.textSecondary)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                    if !model.history.isEmpty {
                        recent
                    }
                }
                .transition(.opacity)
            } else if let result = model.result {
                VStack(spacing: 0) {
                    bar
                        .padding(.horizontal, 16)
                        .padding(.vertical, 10)
                    Rectangle().fill(Palette.separator).frame(height: 1)
                    loaded(result)
                }
                .transition(.opacity)
            }
        }
        .animation(Motion.standard, value: model.result == nil)
    }

    private var bar: some View {
        CommandBar(placeholder: "Paste a blocked link", text: $model.input,
                   action: "Open", isWorking: model.isWorking,
                   onRun: load, onCancel: { model.cancel() }) {
            BarButton(system: "slider.horizontal.3", help: "Methods to try") {
                showStrategyOptions.toggle()
            }
            .popover(isPresented: $showStrategyOptions, arrowEdge: .bottom) {
                strategyPicker
            }
        }
    }

    private var recent: some View {
        VStack(alignment: .leading, spacing: 6) {
            Text("Recent")
                .font(.app(12, .medium))
                .foregroundStyle(Palette.textSecondary)
            FlowRow(spacing: 6) {
                ForEach(model.history, id: \.self) { entry in
                    PillButton(shortLabel(entry), style: .outlined, size: .small) {
                        model.input = entry
                        load()
                    }
                }
            }
        }
    }

    private var strategyPicker: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Methods to try")
                .font(.app(14, .semibold))
                .foregroundStyle(Palette.text)
            ForEach(RelayStrategy.allCases) { strategy in
                Toggle(isOn: Binding(
                    get: { model.enabled.contains(strategy) },
                    set: { on in
                        if on { model.enabled.insert(strategy) }
                        else { model.enabled.remove(strategy) }
                    }
                )) {
                    VStack(alignment: .leading, spacing: 2) {
                        Text(strategy.rawValue)
                            .font(.app(13, .medium))
                            .foregroundStyle(Palette.text)
                        Text(strategy.explanation)
                            .font(.app(11))
                            .foregroundStyle(Palette.textSecondary)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                }
                .toggleStyle(.pill)
            }
        }
        .padding(16)
        .frame(width: 360)
        .background(Palette.surface)
    }

    @ViewBuilder
    private func loaded(_ result: RelayResult) -> some View {
        resultBar(result)
        Rectangle().fill(Palette.separator).frame(height: 1)
        if result.isHTML {
            if appLikeFailure {
                appLikeNotice
            }
            // Everything the page pulls in is routed back through the relay,
            // so styling and images survive the block too.
            RelayWebView(
                url: result.finalURL,
                seedBody: result.rawBody,
                seedContentType: result.contentType,
                onAppLikeFailure: { appLikeFailure = true }
            )
            .id(result.finalURL)
        } else {
            ScrollView {
                Text(result.content)
                    .font(.app(13))
                    .textSelection(.enabled)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .padding(18)
            }
        }
    }

    private func resultBar(_ result: RelayResult) -> some View {
        HStack(spacing: 10) {
            Label(result.strategy.rawValue, systemImage: "checkmark.circle")
                .foregroundStyle(Palette.textSecondary)
                .font(.app(12.5))

            if !result.strategy.returnsLiveDocument {
                Text("· text only")
                    .font(.app(12))
                    .foregroundStyle(Palette.textSecondary)
                    .help("No logins, scripts or interaction with this method")
            }

            Spacer()

            if !result.attemptLog.isEmpty {
                Menu {
                    ForEach(result.attemptLog, id: \.self) { line in
                        Text(line)
                    }
                } label: {
                    Text("\(result.attemptLog.count) failed first")
                        .font(.app(11.5))
                }
                .menuStyle(.borderlessButton)
                .fixedSize()
            }

            CopyButton(text: result.content)
                .controlSize(.small)

            BarButton(system: "safari", help: "Open in your default browser") {
                NSWorkspace.shared.open(result.finalURL)
            }
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 6)
    }

    private func shortLabel(_ urlString: String) -> String {
        guard let host = URL(string: urlString)?.host else { return urlString }
        return host.replacingOccurrences(of: "www.", with: "")
    }
}
