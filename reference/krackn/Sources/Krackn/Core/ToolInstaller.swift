import Foundation

/// An external binary the app can use, and how to obtain it without the user
/// ever opening a Terminal.
struct ManagedTool: Identifiable, Hashable {
    let id: String            // binary name, e.g. "ffmpeg"
    let title: String
    let purpose: String
    /// Direct download of a self-contained binary (or archive) — used first so
    /// the user needs neither Homebrew nor Xcode tools.
    let directURL: String?
    /// Whether the download is a .zip/.tar that must be expanded.
    let archived: Bool
    /// Homebrew formula, used when Homebrew is present and a direct build isn't.
    let brewFormula: String?
    /// A pip package, for the Python-based converters.
    let pipPackage: String?

    static let ffmpeg = ManagedTool(
        id: "ffmpeg", title: "FFmpeg",
        purpose: "Audio and video conversion",
        // evermeet.cx publishes signed, static macOS builds — no dependencies.
        directURL: "https://evermeet.cx/ffmpeg/getrelease/zip",
        archived: true, brewFormula: "ffmpeg", pipPackage: nil)

    static let ffprobe = ManagedTool(
        id: "ffprobe", title: "FFprobe",
        purpose: "Reads media metadata",
        directURL: "https://evermeet.cx/ffmpeg/getrelease/ffprobe/zip",
        archived: true, brewFormula: "ffmpeg", pipPackage: nil)

    static let ytdlp = ManagedTool(
        id: "yt-dlp", title: "yt-dlp",
        purpose: "Video and audio downloads",
        // Official single-file macOS build.
        directURL: "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp_macos",
        archived: false, brewFormula: "yt-dlp", pipPackage: nil)

    static let imagemagick = ManagedTool(
        id: "magick", title: "ImageMagick",
        purpose: "Extended image formats (SVG, RAW, PSD…)",
        directURL: nil, archived: false,
        brewFormula: "imagemagick", pipPackage: nil)

    static let pandoc = ManagedTool(
        id: "pandoc", title: "Pandoc",
        purpose: "Documents: Markdown, LaTeX, ePub, DOCX…",
        directURL: nil, archived: false,
        brewFormula: "pandoc", pipPackage: nil)

    static let meshio = ManagedTool(
        id: "meshio", title: "meshio",
        purpose: "3D meshes: STL, MSH, VTK, OBJ, PLY…",
        directURL: nil, archived: false,
        brewFormula: nil, pipPackage: "meshio[all]")

    static let all: [ManagedTool] = [
        .ffmpeg, .ffprobe, .ytdlp, .imagemagick, .pandoc, .meshio,
    ]
}

enum InstallState: Equatable {
    case notInstalled
    case installing(String)   // human-readable progress
    case installed(String)    // resolved path
    case failed(String)
}

/// Installs the optional command-line tools the app leans on, without asking
/// the user to touch a Terminal.
///
/// Everything lands in the app's own Application Support directory rather than
/// a system location, so no admin password is ever required and uninstalling is
/// just deleting that folder.
@MainActor
final class ToolInstaller: ObservableObject {
    static let shared = ToolInstaller()

    @Published private(set) var states: [String: InstallState] = [:]

    /// Where downloaded binaries live. Added to the lookup path used by
    /// `ShellRunner`, so the rest of the app finds them automatically.
    static let binDirectory: URL = {
        let base = FileManager.default.urls(for: .applicationSupportDirectory,
                                            in: .userDomainMask)[0]
            .appendingPathComponent("Krackn/bin", isDirectory: true)
        try? FileManager.default.createDirectory(at: base, withIntermediateDirectories: true)
        return base
    }()

    /// Deliberately does no work. Detection touches `@Published` state, and
    /// doing that while a view is being constructed produces an AttributeGraph
    /// cycle — SwiftUI reads the state, the read mutates it, and the graph
    /// re-enters. `refreshAll()` is called once from `.task` instead.
    private init() {}

    private var hasScanned = false

    // MARK: - Detection

    func refreshAll() {
        for tool in ManagedTool.all {
            states[tool.id] = resolve(tool)
        }
        hasScanned = true
    }

    /// Safe to call from `.task`/`.onAppear`; scans only the first time.
    func refreshIfNeeded() {
        guard !hasScanned else { return }
        refreshAll()
    }

    func state(of tool: ManagedTool) -> InstallState {
        states[tool.id] ?? .notInstalled
    }

    func isInstalled(_ tool: ManagedTool) -> Bool {
        if case .installed = state(of: tool) { return true }
        return false
    }

    /// The app's own Python environment. Modern macOS Pythons are
    /// "externally managed" (PEP 668) and refuse `pip install`, so packages go
    /// into a private virtualenv here instead of anywhere system-wide.
    static let venvDirectory: URL = {
        FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("Krackn/python", isDirectory: true)
    }()

    /// The interpreter to use for Python-backed conversions: the app's venv if
    /// it exists, otherwise whatever python3 is on the system.
    static var pythonPath: String? {
        let venvPython = venvDirectory.appendingPathComponent("bin/python3").path
        if FileManager.default.isExecutableFile(atPath: venvPython) { return venvPython }
        return ShellRunner.which("python3")
    }

    private func resolve(_ tool: ManagedTool) -> InstallState {
        // Python packages aren't binaries — ask the interpreter instead.
        if let pip = tool.pipPackage {
            let module = pip.split(separator: "[").first.map(String.init) ?? pip
            if let python = Self.pythonPath,
               runSync(python, ["-c", "import \(module)"]) == 0 {
                return .installed(module)
            }
            return .notInstalled
        }
        if let path = ShellRunner.which(tool.id) { return .installed(path) }
        return .notInstalled
    }

    private func runSync(_ launch: String, _ args: [String]) -> Int32 {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: launch)
        process.arguments = args
        process.standardOutput = FileHandle.nullDevice
        process.standardError = FileHandle.nullDevice
        do { try process.run() } catch { return -1 }
        process.waitUntilExit()
        return process.terminationStatus
    }

    // MARK: - Install

    /// Tries, in order: a direct static-binary download, then pip, then
    /// Homebrew. The first path needs nothing pre-installed, which is why it
    /// comes first — most users will never hit the fallbacks.
    /// Re-fetches a tool that's already present.
    ///
    /// `install` deliberately does nothing when a tool is found, which is right
    /// for a one-off dependency and wrong for yt-dlp: YouTube changes its
    /// player signatures every few weeks and a copy that worked in July returns
    /// HTTP 403 in August. Without this the app could only ever report that
    /// failure, never fix it — and a Homebrew copy the app didn't install is
    /// exactly the one most likely to be stale.
    func update(_ tool: ManagedTool) async {
        // Force the managed copy to be rebuilt from the direct download, so the
        // fresh binary takes precedence over any older one on the PATH — the
        // app's own bin directory is searched first.
        states[tool.id] = .installing("Updating \(tool.title)…")
        if let direct = tool.directURL, await downloadBinary(tool, from: direct) {
            states[tool.id] = resolve(tool)
            if isInstalled(tool) { return }
        }
        if let pip = tool.pipPackage, await installViaPip(pip, toolID: tool.id) {
            states[tool.id] = resolve(tool)
            if isInstalled(tool) { return }
        }
        if let formula = tool.brewFormula, ShellRunner.which("brew") != nil {
            states[tool.id] = .installing("Upgrading \(tool.title) via Homebrew…")
            _ = try? await ShellRunner.run(ShellRunner.which("brew")!, ["upgrade", formula])
            states[tool.id] = resolve(tool)
            if isInstalled(tool) { return }
        }
        states[tool.id] = .failed("Couldn't update automatically.")
    }

    /// The version string a tool reports, for showing how stale it is.
    func version(of tool: ManagedTool) async -> String? {
        guard let path = ShellRunner.which(tool.id) else { return nil }
        guard let result = try? await ShellRunner.run(path, ["--version"]) else { return nil }
        let text = result.stdout.trimmingCharacters(in: .whitespacesAndNewlines)
        return text.isEmpty ? nil : text.split(separator: "\n").first.map(String.init)
    }

    func install(_ tool: ManagedTool) async {
        guard !isInstalled(tool) else { return }
        states[tool.id] = .installing("Starting…")

        if let direct = tool.directURL {
            states[tool.id] = .installing("Downloading \(tool.title)…")
            if await downloadBinary(tool, from: direct) {
                states[tool.id] = resolve(tool)
                if isInstalled(tool) { return }
            }
        }

        if let pip = tool.pipPackage {
            states[tool.id] = .installing("Installing \(tool.title) via pip…")
            if await installViaPip(pip, toolID: tool.id) {
                states[tool.id] = resolve(tool)
                if isInstalled(tool) { return }
            }
        }

        if let formula = tool.brewFormula {
            if ShellRunner.which("brew") != nil {
                states[tool.id] = .installing("Installing \(tool.title) via Homebrew…")
                if await installViaBrew(formula) {
                    states[tool.id] = resolve(tool)
                    if isInstalled(tool) { return }
                }
            } else {
                states[tool.id] = .failed(
                    "Needs Homebrew. Install it from brew.sh, then press Install again.")
                return
            }
        }

        if !isInstalled(tool) {
            states[tool.id] = .failed("Couldn't install automatically.")
        }
    }

    private func downloadBinary(_ tool: ManagedTool, from urlString: String) async -> Bool {
        guard let url = URL(string: urlString) else { return false }
        do {
            let (tempFile, response) = try await URLSession.shared.download(from: url)
            if let http = response as? HTTPURLResponse, !(200..<300).contains(http.statusCode) {
                states[tool.id] = .failed("Download failed (HTTP \(http.statusCode)).")
                return false
            }

            let destination = Self.binDirectory.appendingPathComponent(tool.id)
            try? FileManager.default.removeItem(at: destination)

            if tool.archived {
                states[tool.id] = .installing("Extracting…")
                let unzipDir = Self.binDirectory.appendingPathComponent("_tmp_\(tool.id)")
                try? FileManager.default.removeItem(at: unzipDir)
                try FileManager.default.createDirectory(at: unzipDir,
                                                        withIntermediateDirectories: true)
                let zipPath = unzipDir.appendingPathComponent("download.zip")
                try FileManager.default.moveItem(at: tempFile, to: zipPath)
                _ = try await ShellRunner.run("/usr/bin/ditto",
                                              ["-x", "-k", zipPath.path, unzipDir.path])
                // The archive holds the binary at some depth; find it by name.
                if let found = locate(tool.id, under: unzipDir) {
                    try FileManager.default.moveItem(at: found, to: destination)
                }
                try? FileManager.default.removeItem(at: unzipDir)
            } else {
                try FileManager.default.moveItem(at: tempFile, to: destination)
            }

            guard FileManager.default.fileExists(atPath: destination.path) else { return false }
            try FileManager.default.setAttributes([.posixPermissions: 0o755],
                                                  ofItemAtPath: destination.path)
            // Downloaded files carry a quarantine flag that blocks execution
            // until it's cleared.
            _ = try? await ShellRunner.run("/usr/bin/xattr",
                                           ["-d", "com.apple.quarantine", destination.path])
            return true
        } catch {
            states[tool.id] = .failed(error.localizedDescription)
            return false
        }
    }

    private func locate(_ name: String, under directory: URL) -> URL? {
        guard let walker = FileManager.default.enumerator(
            at: directory, includingPropertiesForKeys: nil) else { return nil }
        for case let url as URL in walker where url.lastPathComponent == name {
            return url
        }
        return nil
    }

    /// Installs into the app's private virtualenv, creating it on first use.
    ///
    /// `pip install --user` fails on current macOS: the system Python is marked
    /// externally managed (PEP 668) and refuses to be modified. A venv is both
    /// the supported route and safer — nothing outside our folder is touched.
    private func installViaPip(_ package: String, toolID: String) async -> Bool {
        guard let systemPython = ShellRunner.which("python3") else { return false }

        let venvPython = Self.venvDirectory.appendingPathComponent("bin/python3").path
        if !FileManager.default.isExecutableFile(atPath: venvPython) {
            // Keyed by the tool's id, not the pip package spec: the row watches
            // `states["meshio"]`, so writing `states["meshio[all]"]` here meant
            // the "Creating Python environment…" step never appeared.
            states[toolID] = .installing("Creating Python environment…")
            let created = try? await ShellRunner.run(
                systemPython, ["-m", "venv", Self.venvDirectory.path])
            guard created?.ok == true else { return false }
        }

        let result = try? await ShellRunner.run(
            venvPython, ["-m", "pip", "install", "--upgrade", package])
        return result?.ok ?? false
    }

    private func installViaBrew(_ formula: String) async -> Bool {
        guard let brew = ShellRunner.which("brew") else { return false }
        let result = try? await ShellRunner.run(brew, ["install", formula])
        return result?.ok ?? false
    }

    /// Installs everything missing, for the one-click "Set up all tools" button.
    func installAllMissing() async {
        for tool in ManagedTool.all where !isInstalled(tool) {
            await install(tool)
        }
    }
}
