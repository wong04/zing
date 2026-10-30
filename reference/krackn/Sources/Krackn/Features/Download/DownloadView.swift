import SwiftUI

@MainActor
final class DownloadModel: ObservableObject {
    enum Mode: String, CaseIterable, Identifiable {
        case video = "Video"
        case audio = "Audio only"
        case page = "Save page"

        var id: String { rawValue }
    }

    @Published var url = ""
    @Published var mode: Mode = .video
    @Published var destination: URL = FileManager.default
        .urls(for: .downloadsDirectory, in: .userDomainMask)[0]
    @Published var log: [String] = []
    @Published var isWorking = false
    @Published var finishedMessage: String?
    @Published var errorText: String?
    @Published var ytdlpInstalled = ShellRunner.which("yt-dlp") != nil
    /// Set when a failure is the kind a newer yt-dlp fixes, so the UI can offer
    /// the update instead of leaving the user with an HTTP status code.
    @Published var suggestsUpdate = false
    @Published var ytdlpVersion: String?

    func recheckTools() {
        ytdlpInstalled = ShellRunner.which("yt-dlp") != nil
        Task { ytdlpVersion = await ToolInstaller.shared.version(of: .ytdlp) }
    }

    func start() async {
        guard let target = try? RelayEngine.normalize(url) else {
            errorText = "That doesn't look like a URL."
            return
        }
        isWorking = true
        log = []
        errorText = nil
        finishedMessage = nil
        suggestsUpdate = false
        defer { isWorking = false }

        if mode == .page {
            await savePage(target)
            return
        }

        guard ytdlpInstalled else {
            errorText = "yt-dlp isn't installed yet. Press Install above."
            return
        }

        var arguments = [
            "--newline",                   // one progress line per update, not \r spam
            "--no-playlist",
            "-o", destination.appendingPathComponent("%(title)s.%(ext)s").path,
        ]

        switch mode {
        case .audio:
            arguments += ["-x", "--audio-format", "mp3", "--audio-quality", "0"]
        case .video:
            // Prefer a pre-merged mp4 so we don't hard-depend on ffmpeg being
            // present to stitch separate video and audio streams together.
            arguments += ["-f", "best[ext=mp4]/bestvideo+bestaudio/best"]
        case .page:
            break
        }
        arguments.append(target.absoluteString)

        do {
            var result = try await runYtDlp(arguments)

            // A 403 on the media URL almost always means the player client
            // yt-dlp picked handed back links that don't validate — YouTube
            // rotates this constantly. Asking for a different client is the
            // standard remedy and costs one retry, so do it automatically
            // rather than showing the user a bare HTTP code.
            if !result.ok, DownloadErrors.looksLikeExtractorBreakage(result.stderr) {
                log.append("— HTTP 403 on the media. Retrying with a different "
                           + "YouTube player client…")
                var retry = arguments
                // `default` only. Adding web_safari and tv looked like a
                // broader net but measurably made things worse: on a real
                // video they fail with "requested format is not available"
                // and "the page needs to be reloaded", so a retry that
                // included them could turn a transient 403 into a hard error.
                retry.insert(contentsOf: [
                    "--extractor-args", "youtube:player_client=default",
                ], at: 0)
                result = try await runYtDlp(retry)
            }

            if result.ok {
                finishedMessage = "Saved to \(destination.lastPathComponent)."
            } else {
                errorText = DownloadErrors.explain(result.stderr, exitCode: result.exitCode)
                // Offer the one fix that actually resolves a broken extractor.
                suggestsUpdate = DownloadErrors.looksLikeExtractorBreakage(result.stderr)
            }
        } catch {
            errorText = error.localizedDescription
        }
    }

    private func runYtDlp(_ arguments: [String]) async throws -> ShellResult {
        try await ShellRunner.run("yt-dlp", arguments) { [weak self] line in
            Task { @MainActor in
                guard let self else { return }
                self.log.append(line)
                if self.log.count > 200 { self.log.removeFirst(self.log.count - 200) }
            }
        }
    }

    /// Saves a readable copy of a page. Routed through the relay engine so this
    /// works on blocked sites too, which is the whole point of the app.
    private func savePage(_ target: URL) async {
        let engine = RelayEngine()
        do {
            log.append("Fetching…")
            let result = try await engine.fetch(
                urlString: target.absoluteString,
                enabled: Set(RelayStrategy.allCases),
                progress: { [weak self] note in
                    Task { @MainActor in self?.log.append(note) }
                }
            )
            let host = target.host ?? "page"
            let ext = result.isHTML ? "html" : "md"
            var file = destination.appendingPathComponent("\(host).\(ext)")
            var counter = 2
            while FileManager.default.fileExists(atPath: file.path) {
                file = destination.appendingPathComponent("\(host) \(counter).\(ext)")
                counter += 1
            }
            // Write the bytes the server actually sent. Re-encoding through a
            // Swift String would force UTF-8 and mangle any page served as
            // Latin-1 or Windows-1252 — and the saved file's own meta charset
            // would then contradict its contents.
            try result.rawBody.write(to: file, options: .atomic)
            finishedMessage = "Saved \(file.lastPathComponent) via \(result.strategy.rawValue)."
        } catch {
            errorText = error.localizedDescription
        }
    }
}

struct DownloadView: View {
    @StateObject private var model = DownloadModel()
    @ObservedObject private var installer = ToolInstaller.shared

    var body: some View {
        PageContainer {
            CommandBar(placeholder: "Paste a video or page link", text: $model.url,
                       action: "Download", actionIcon: "arrow.down",
                       isWorking: model.isWorking,
                       onRun: { Task { await model.start() } })

            HStack(spacing: 12) {
                SegmentedControl(
                    options: DownloadModel.Mode.allCases.map { ($0, $0.rawValue) },
                    selection: $model.mode
                )
                .frame(maxWidth: 320)
                Spacer(minLength: 8)
                Button { pickDestination() } label: {
                    HStack(spacing: 5) {
                        Image(systemName: "folder").font(.system(size: 11.5))
                        Text(model.destination.lastPathComponent).font(.app(12.5))
                    }
                    .foregroundStyle(Palette.textSecondary)
                }
                .buttonStyle(PressSettleStyle())
                .help("Change where downloads are saved")
            }

            if model.mode != .page && !model.ytdlpInstalled {
                HStack(spacing: 10) {
                    Text("Video and audio need yt-dlp.")
                        .font(.app(12.5))
                        .foregroundStyle(Palette.textSecondary)
                    Spacer()
                    if case .installing(let note) = installer.state(of: .ytdlp) {
                        ProgressView().controlSize(.small)
                        Text(note).font(.app(12)).foregroundStyle(Palette.textSecondary)
                    } else {
                        PillButton("Install", style: .outlined, size: .small) {
                            Task {
                                await installer.install(.ytdlp)
                                model.recheckTools()
                            }
                        }
                    }
                }
                .transition(.opacity)
            }

            if let message = model.finishedMessage {
                InfoBanner(kind: .success, text: message)
            }
            if let error = model.errorText {
                InfoBanner(kind: .warning, text: error)
            }

            // The remedy, next to the failure it fixes. yt-dlp is the one tool
            // here that must stay current, so "installed" isn't "working".
            if model.suggestsUpdate {
                SectionCard(title: "Update yt-dlp") {
                    VStack(alignment: .leading, spacing: 10) {
                        Text("YouTube changes how it signs media links every few weeks; "
                             + "an older yt-dlp then gets HTTP 403."
                             + (model.ytdlpVersion.map { " Installed: \($0)." } ?? ""))
                            .font(.app(12))
                            .foregroundStyle(Palette.textSecondary)
                            .fixedSize(horizontal: false, vertical: true)

                        if case .installing(let note) = installer.state(of: .ytdlp) {
                            HStack(spacing: 8) {
                                ProgressView().controlSize(.small)
                                Text(note).font(.app(12)).foregroundStyle(Palette.textSecondary)
                            }
                        } else {
                            HStack(spacing: 8) {
                                PillButton("Update", style: .filled, size: .small) {
                                    Task {
                                        await installer.update(.ytdlp)
                                        model.recheckTools()
                                        model.suggestsUpdate = false
                                    }
                                }
                                PillButton("Try Again", style: .outlined, size: .small) {
                                    Task { await model.start() }
                                }
                            }
                        }
                    }
                }
            }

            if !model.log.isEmpty {
                SectionCard(title: "Progress") {
                    ScrollView {
                        VStack(alignment: .leading, spacing: 1) {
                            ForEach(Array(model.log.enumerated()), id: \.offset) { _, line in
                                Text(line)
                                    .font(.system(size: 11, design: .monospaced))
                                    .foregroundStyle(Palette.textSecondary)
                                    .frame(maxWidth: .infinity, alignment: .leading)
                            }
                        }
                    }
                    .frame(height: 160)
                }
                .transition(.opacity)
            }
        }
        .animation(Motion.standard, value: model.log.isEmpty)
        .animation(Motion.standard, value: model.mode)
        .task { model.recheckTools() }
    }

    private func pickDestination() {
        let panel = NSOpenPanel()
        panel.canChooseFiles = false
        panel.canChooseDirectories = true
        if panel.runModal() == .OK, let url = panel.url {
            model.destination = url
        }
    }
}
