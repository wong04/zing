import SwiftUI
import UniformTypeIdentifiers

@MainActor
final class ConvertModel: ObservableObject {
    @Published var files: [URL] = []
    @Published var category: ConversionCategory = .image
    @Published var target: ConversionFormat
    /// Seeded from the Settings default rather than a hardcoded 80, which is
    /// what made the "Default quality" slider in Settings do nothing at all.
    @Published var quality: Double = Double(AppSettings.defaultQuality)
    /// Defaults to Downloads. Output used to land beside each source file,
    /// which scattered results across wherever the input happened to live.
    @Published var outputDirectory: URL? = AppSettings.downloadsDirectory
    @Published var outcomes: [ConversionOutcome] = []
    @Published var isWorking = false
    @Published var progressNote = ""
    @Published var ffmpegInstalled = ShellRunner.which("ffmpeg") != nil
    /// Files converted earlier this session, offered as one-tap shortcuts —
    /// the same affordance the file viewer has.
    @Published var recents: [URL] = []
    /// Outputs already saved to Downloads. One copy per conversion: the button
    /// locks after use and only unlocks when the file is converted afresh.
    @Published var downloaded: Set<URL> = []

    init() {
        target = ConversionCategory.image.outputFormats[0]
    }

    /// Picks the category from whatever was dropped, so the user doesn't have
    /// to tell us that a .mov is a video.
    func add(_ urls: [URL]) {
        let fresh = urls.filter { url in !files.contains(url) }
        files.append(contentsOf: fresh)
        if let first = fresh.first, let detected = ConversionEngine.category(for: first) {
            if detected != category {
                category = detected
                target = detected.outputFormats[0]
            }
        }
        if outputDirectory == nil {
            outputDirectory = AppSettings.downloadsDirectory
        }
        for url in fresh {
            recents.removeAll { $0 == url }
            recents.insert(url, at: 0)
        }
        recents = Array(recents.prefix(10))
    }

    /// Clears the queue and results but keeps the recents list.
    func reset() {
        files = []
        outcomes = []
        downloaded = []
        progressNote = ""
    }

    /// Copies a converted file into the user's Downloads folder.
    ///
    /// Returns false if the copy fails, so the button only locks on a real
    /// success — a failed save shouldn't leave the user unable to retry.
    @discardableResult
    func saveToDownloads(_ url: URL) -> Bool {
        let downloads = FileManager.default
            .urls(for: .downloadsDirectory, in: .userDomainMask)[0]

        // Already sitting in Downloads — nothing to copy, just record it.
        guard url.deletingLastPathComponent().standardizedFileURL
                != downloads.standardizedFileURL else {
            downloaded.insert(url)
            return true
        }

        var destination = downloads.appendingPathComponent(url.lastPathComponent)
        var counter = 2
        let base = destination.deletingPathExtension().lastPathComponent
        let ext = destination.pathExtension
        while FileManager.default.fileExists(atPath: destination.path) {
            let name = ext.isEmpty ? "\(base) \(counter)" : "\(base) \(counter).\(ext)"
            destination = downloads.appendingPathComponent(name)
            counter += 1
        }

        do {
            try FileManager.default.copyItem(at: url, to: destination)
            downloaded.insert(url)
            return true
        } catch {
            return false
        }
    }

    func selectCategory(_ new: ConversionCategory) {
        category = new
        target = new.outputFormats[0]
    }

    func run() async {
        guard !files.isEmpty else { return }
        let destination = outputDirectory ?? AppSettings.downloadsDirectory

        isWorking = true
        outcomes = []
        // A new run produces new outputs, so previous download locks no longer
        // apply.
        downloaded = []
        defer { isWorking = false; progressNote = "" }

        for (index, file) in files.enumerated() {
            progressNote = "Converting \(index + 1) of \(files.count): \(file.lastPathComponent)"
            do {
                let output = try await ConversionEngine.convert(
                    source: file,
                    to: target,
                    outputDirectory: destination,
                    quality: Int(quality),
                    onProgress: { [weak self] line in
                        // ffmpeg's per-frame chatter is noise; only surface the
                        // lines that carry a timestamp.
                        guard line.contains("time=") else { return }
                        Task { @MainActor in self?.progressNote = line }
                    }
                )
                outcomes.append(.init(source: file, destination: output, error: nil))
            } catch {
                outcomes.append(.init(source: file, destination: nil,
                                      error: error.localizedDescription))
            }
        }
    }
}

struct ConvertView: View {
    /// Owned by RootView, so a queue and its results survive navigating away.
    @ObservedObject var model: ConvertModel
    @ObservedObject private var installer = ToolInstaller.shared
    @State private var isTargeted = false
    /// A file sent over from the File Viewer's "Convert" button. Consumed once
    /// on appear, then cleared so switching back doesn't re-add it.
    var pendingFile: Binding<URL?> = .constant(nil)
    /// Opens a converted file in the Files previewer.
    var onPreview: ((URL) -> Void)?
    @State private var downloadError: String?

    var body: some View {
        Group {
            if model.files.isEmpty && model.outcomes.isEmpty {
                emptyState
            } else {
                PageContainer {
                    dropZone
                    if !model.files.isEmpty {
                        fileList
                        settings
                        actions
                    }
                    if !model.outcomes.isEmpty {
                        results
                    }
                    if ManagedTool.all.contains(where: { !installer.isInstalled($0) }) {
                        ToolSetupView()
                    }
                }
            }
        }
        .onDrop(of: DropRouter.acceptedTypes, isTargeted: $isTargeted) { providers in
            Task {
                let urls = await DropRouter.resolve(providers)
                guard !urls.isEmpty else { return }
                await MainActor.run { model.add(urls) }
            }
            return true
        }
        .task { installer.refreshIfNeeded() }
        .onAppear { consumePending() }
        // `.onAppear` alone wasn't enough: dropping a file on the dock's
        // Convert row while Convert was already the visible section set the
        // pending file without ever re-appearing, so the drop was silently
        // discarded. The viewer already guarded this; the converter didn't.
        .onChange(of: pendingFile.wrappedValue) { _, _ in consumePending() }
    }

    private func consumePending() {
        guard let file = pendingFile.wrappedValue else { return }
        model.add([file])
        pendingFile.wrappedValue = nil
    }

    /// The same empty state the viewer and background remover use, so the
    /// three tools read as a set rather than three unrelated screens.
    private var emptyState: some View {
        ToolEmptyState(
            icon: "arrow.triangle.2.circlepath",
            title: "Convert files",
            message: "Drop files here, or choose them. \(FormatCatalog.allInputs.count) formats.",
            isTargeted: isTargeted,
            recents: model.recents,
            onSelectRecent: { model.add([$0]) }
        ) {
            PillButton("Choose Files", icon: "folder", style: .filled) { pickFiles() }
        }
    }

    private var dropZone: some View {
        VStack(spacing: 12) {
            Image(systemName: "arrow.down.doc")
                .font(.app(32))
                .foregroundStyle(isTargeted ? Palette.accent : Palette.textSecondary)
            Text(model.files.isEmpty ? "Drop files here" : "Drop more files")
                .font(.app(14))
                .foregroundStyle(Palette.textSecondary)
            PillButton("Choose Files", icon: "folder", style: .outlined, size: .small) {
                pickFiles()
            }
        }
        .frame(maxWidth: .infinity)
        .padding(.vertical, 34)
        .background(
            RoundedRectangle(cornerRadius: 16)
                .fill(isTargeted ? Palette.accent.opacity(0.08) : Color.clear)
        )
        .background(
            RoundedRectangle(cornerRadius: 16)
                .strokeBorder(
                    isTargeted ? Palette.accent : Palette.border,
                    style: StrokeStyle(lineWidth: 1.5, dash: [7, 5])
                )
        )
        .onDrop(of: DropRouter.acceptedTypes, isTargeted: $isTargeted) { providers in
            // Handles Finder files, raw image bytes dragged from a page, and
            // plain web URLs (which get downloaded first).
            Task {
                let urls = await DropRouter.resolve(providers)
                guard !urls.isEmpty else { return }
                await MainActor.run { model.add(urls) }
            }
            return true
        }
    }

    private var fileList: some View {
        SectionCard(title: "\(model.files.count) file\(model.files.count == 1 ? "" : "s")") {
            VStack(alignment: .leading, spacing: 4) {
                ForEach(model.files, id: \.self) { file in
                    HStack {
                        FileRow(url: file, iconSize: 18)
                        Button {
                            model.files.removeAll { $0 == file }
                        } label: {
                            Image(systemName: "xmark.circle.fill")
                        }
                        .buttonStyle(.borderless)
                        .foregroundStyle(Palette.textSecondary.opacity(0.7))
                    }
                    .padding(.vertical, 2)
                    // Queued files can be dragged back out — into the viewer,
                    // onto a browser tab's upload field, or into Finder.
                    .onDrag { NSItemProvider.file(file) }
                }
            }
        }
    }

    private var settings: some View {
        SectionCard(title: "Output") {
            VStack(alignment: .leading, spacing: 12) {
                SegmentedControl(
                    options: ConversionCategory.allCases.map { ($0, $0.rawValue) },
                    selection: Binding(
                        get: { model.category },
                        set: { model.selectCategory($0) }
                    ))

                HStack {
                    Text("Convert to")
                        .font(.app(13))
                        .foregroundStyle(Palette.textSecondary)
                    PillMenu(
                        options: model.category.outputFormats.map { ($0, $0.label) },
                        selection: $model.target)
                }

                if let tool = model.target.backend.managedTool,
                   !ToolInstaller.shared.isInstalled(tool) {
                    VStack(alignment: .leading, spacing: 9) {
                        InfoBanner(
                            kind: .info,
                            text: "This format uses **\(tool.title)**. The app can install it "
                                + "for you — no Terminal needed."
                        )
                        if case .installing(let note) = ToolInstaller.shared.state(of: tool) {
                            HStack(spacing: 8) {
                                ProgressView().controlSize(.small)
                                Text(note).font(.app(12))
                                    .foregroundStyle(Palette.textSecondary)
                            }
                        } else {
                            PillButton("Install \(tool.title)", icon: "arrow.down.circle",
                                       style: .filled, size: .small) {
                                Task { await ToolInstaller.shared.install(tool) }
                            }
                        }
                    }
                }

                if isLossy {
                    VStack(alignment: .leading, spacing: 2) {
                        HStack {
                            Text("Quality")
                            Spacer()
                            Text("\(Int(model.quality))")
                                .foregroundStyle(Palette.textSecondary)
                                .monospacedDigit()
                        }
                        PillSlider(value: $model.quality, range: 10...100, step: 5)
                    }
                }

                HStack {
                    Text("Save to")
                    Spacer()
                    Text(model.outputDirectory?.lastPathComponent ?? "same folder as source")
                        .foregroundStyle(Palette.textSecondary)
                        .lineLimit(1)
                    PillButton("Change", style: .ghost, size: .small) { pickOutputDirectory() }
                }
                .font(.app(13))
            }
        }
    }

    private var actions: some View {
        HStack(spacing: 10) {
            PillButton(
                "Convert \(model.files.count) file\(model.files.count == 1 ? "" : "s")",
                icon: "arrow.right", style: .filled
            ) { Task { await model.run() } }
            .disabled(model.isWorking)

            PillButton("Clear", style: .ghost) {
                model.files = []
                model.outcomes = []
            }
            .disabled(model.isWorking)

            if model.isWorking {
                ProgressView().controlSize(.small)
                Text(model.progressNote)
                    .font(.app(11.5))
                    .foregroundStyle(Palette.textSecondary)
                    .lineLimit(1)
            }
        }
    }

    private var results: some View {
        SectionCard(title: "Results") {
            VStack(alignment: .leading, spacing: 8) {
                if let downloadError {
                    InfoBanner(kind: .warning, text: downloadError)
                }
                ForEach(model.outcomes, id: \.source) { outcome in
                    HStack(alignment: .top, spacing: 7) {
                        Image(systemName: outcome.succeeded
                              ? "checkmark.circle.fill" : "xmark.circle.fill")
                            .foregroundStyle(outcome.succeeded ? Palette.positive : Palette.caution)
                        if let destination = outcome.destination {
                            FileIcon(url: destination, size: 17)
                        }
                        VStack(alignment: .leading, spacing: 1) {
                            Text(outcome.destination?.lastPathComponent
                                 ?? outcome.source.lastPathComponent)
                                .font(.app(13))
                            if let error = outcome.error {
                                Text(error)
                                    .font(.app(11.5))
                                    .foregroundStyle(Palette.textSecondary)
                                    .fixedSize(horizontal: false, vertical: true)
                            }
                        }
                        Spacer()
                        if let destination = outcome.destination {
                            // Opens the result in the app's own previewer
                            // rather than bouncing out to Finder.
                            PillButton("Show", icon: "eye", style: .ghost, size: .small) {
                                onPreview?(destination)
                            }
                            if model.downloaded.contains(destination) {
                                // Latched: one saved copy per conversion.
                                HStack(spacing: 5) {
                                    Image(systemName: "checkmark")
                                        .font(.app(11, .semibold))
                                    Text("Downloaded")
                                        .font(.app(12, .semibold))
                                }
                                .foregroundStyle(Palette.positive)
                                .padding(.horizontal, 13)
                                .padding(.vertical, 6.5)
                                .overlay(Capsule().strokeBorder(
                                    Palette.positive.opacity(0.5), lineWidth: 1))
                            } else {
                                PillButton("Download", icon: "arrow.down",
                                           style: .outlined, size: .small) {
                                    withAnimation(.easeOut(duration: 0.18)) {
                                        if !model.saveToDownloads(destination) {
                                            downloadError = "Couldn't save "
                                                + destination.lastPathComponent
                                                + " to Downloads."
                                        }
                                    }
                                }
                            }
                        }
                    }
                    // A finished conversion is a drag source, so a result can
                    // go straight to the viewer, the background remover, a
                    // browser upload field, or Finder without a round trip.
                    .onDrag {
                        guard let destination = outcome.destination else { return NSItemProvider() }
                        return NSItemProvider.file(destination)
                    }
                }
            }
        }
    }

    private var isLossy: Bool {
        ["jpeg", "mp3", "m4a", "ogg", "mp4", "mov", "webm", "mkv"].contains(model.target.ext)
    }

    private func pickFiles() {
        let panel = NSOpenPanel()
        panel.allowsMultipleSelection = true
        panel.canChooseDirectories = false
        if panel.runModal() == .OK {
            model.add(panel.urls)
        }
    }

    private func pickOutputDirectory() {
        let panel = NSOpenPanel()
        panel.canChooseFiles = false
        panel.canChooseDirectories = true
        panel.allowsMultipleSelection = false
        if panel.runModal() == .OK {
            model.outputDirectory = panel.url
        }
    }
}
