import SwiftUI
import SceneKit
import AVKit
import Quartz

/// How a given file should be displayed. QuickLook is the general fallback, but
/// for the formats this app's audience cares about we use a purpose-built
/// viewer: an .stl becomes a model you can orbit, an .mp4 actually plays.
enum PreviewKind {
    case model3D      // SceneKit — rotatable geometry
    case media        // AVKit — video and audio with transport controls
    case image        // NSImage — zoomable, with pixel dimensions
    case text         // source code, data, logs
    case quickLook    // PDFs, Office, everything else macOS knows

    static func forFile(_ url: URL) -> PreviewKind {
        let ext = url.pathExtension.lowercased()
        if sceneKitFormats.contains(ext) { return .model3D }
        if FormatCatalog.video.contains(ext) || FormatCatalog.audio.contains(ext) {
            return .media
        }
        if FormatCatalog.sipsImages.contains(ext) || ext == "webp" { return .image }
        if textFormats.contains(ext) { return .text }
        return .quickLook
    }

    /// Formats SceneKit reads directly. Other meshes are converted on the fly.
    static let sceneKitFormats: Set<String> = [
        "stl", "obj", "ply", "dae", "usdz", "usd", "usda", "usdc", "scn", "abc",
    ]

    /// Meshes SceneKit can't open, but meshio can convert to one it can.
    static let convertibleMeshes: Set<String> = [
        "msh", "vtk", "vtu", "vtp", "xdmf", "off", "mesh", "med", "bdf",
        "inp", "cgns", "exo", "e", "su2", "neu", "nas", "fem",
    ]

    /// Code and data files worth showing as text rather than an icon.
    static let textFormats: Set<String> = [
        "txt", "md", "markdown", "rst", "log", "csv", "tsv", "json", "yaml",
        "yml", "toml", "ini", "cfg", "conf", "xml", "svg", "html", "css",
        "js", "ts", "jsx", "tsx", "py", "swift", "c", "h", "cpp", "hpp", "cc",
        "m", "mm", "java", "kt", "go", "rs", "rb", "php", "pl", "lua", "r",
        "jl", "sh", "bash", "zsh", "fish", "sql", "graphql", "proto", "tex",
        "bib", "gitignore", "dockerfile", "makefile", "cmake", "gradle",
        "ipynb", "env", "properties", "vue", "svelte", "scss", "sass", "less",
        "asm", "s", "f", "f90", "for", "pas", "vb", "cs", "dart", "scala",
        "clj", "ex", "exs", "erl", "hs", "ml", "nim", "zig", "v", "sv", "vhd",
    ]
}

// MARK: - 3D

/// Interactive 3D view. Orbit with a drag, zoom with the scroll wheel.
struct ModelPreview: NSViewRepresentable {
    let url: URL

    func makeNSView(context: Context) -> SCNView {
        let view = SCNView()
        view.allowsCameraControl = true          // orbit / pan / zoom
        view.autoenablesDefaultLighting = true
        view.antialiasingMode = .multisampling4X
        view.backgroundColor = .clear
        return view
    }

    func updateNSView(_ view: SCNView, context: Context) {
        guard context.coordinator.loaded != url else { return }
        context.coordinator.loaded = url

        guard let scene = try? SCNScene(url: url, options: [.checkConsistency: false]) else {
            return
        }
        // Meshes often arrive without material or lighting; give them a
        // neutral surface so the geometry is actually readable.
        scene.rootNode.enumerateChildNodes { node, _ in
            guard let geometry = node.geometry else { return }
            if geometry.materials.isEmpty || geometry.firstMaterial?.diffuse.contents == nil {
                let material = SCNMaterial()
                material.diffuse.contents = NSColor(calibratedRed: 0.78, green: 0.72,
                                                    blue: 0.60, alpha: 1)
                material.lightingModel = .physicallyBased
                material.roughness.contents = 0.55
                material.metalness.contents = 0.05
                geometry.materials = [material]
            }
        }
        // SceneKit's default camera sits at a fixed distance regardless of the
        // model's size, so a small mesh fills the frame and a large one is a
        // speck. Compute the bounding sphere and place the camera from that.
        let (center, radius) = boundingSphere(of: scene.rootNode)
        let camera = SCNCamera()
        camera.zNear = 0.001
        camera.zFar = Double(radius) * 100 + 100
        camera.fieldOfView = 45

        let cameraNode = SCNNode()
        cameraNode.camera = camera
        // 2.2 radii back gives a comfortable margin around the model.
        let distance = max(radius * 2.2, 0.5)
        cameraNode.position = SCNVector3(
            center.x + distance * 0.6,
            center.y + distance * 0.5,
            center.z + distance)
        cameraNode.look(at: center)
        scene.rootNode.addChildNode(cameraNode)

        view.scene = scene
        view.pointOfView = cameraNode
    }

    /// Centre and radius covering every geometry node in the scene.
    private func boundingSphere(of root: SCNNode) -> (SCNVector3, CGFloat) {
        var minimum = SCNVector3(x: .greatestFiniteMagnitude,
                                 y: .greatestFiniteMagnitude,
                                 z: .greatestFiniteMagnitude)
        var maximum = SCNVector3(x: -.greatestFiniteMagnitude,
                                 y: -.greatestFiniteMagnitude,
                                 z: -.greatestFiniteMagnitude)
        var found = false

        root.enumerateHierarchy { node, _ in
            guard node.geometry != nil else { return }
            let (localMin, localMax) = node.boundingBox
            // Corners must be converted individually: a rotated child's box
            // doesn't map corner-to-corner.
            for x in [localMin.x, localMax.x] {
                for y in [localMin.y, localMax.y] {
                    for z in [localMin.z, localMax.z] {
                        let world = node.convertPosition(SCNVector3(x, y, z), to: root)
                        minimum = SCNVector3(min(minimum.x, world.x),
                                             min(minimum.y, world.y),
                                             min(minimum.z, world.z))
                        maximum = SCNVector3(max(maximum.x, world.x),
                                             max(maximum.y, world.y),
                                             max(maximum.z, world.z))
                        found = true
                    }
                }
            }
        }

        guard found else { return (SCNVector3Zero, 1) }
        let center = SCNVector3((minimum.x + maximum.x) / 2,
                                (minimum.y + maximum.y) / 2,
                                (minimum.z + maximum.z) / 2)
        let extent = SCNVector3(maximum.x - minimum.x,
                                maximum.y - minimum.y,
                                maximum.z - minimum.z)
        let radius = CGFloat(max(extent.x, max(extent.y, extent.z))) / 2
        return (center, max(radius, 0.001))
    }

    func makeCoordinator() -> Coordinator { Coordinator() }
    final class Coordinator { var loaded: URL? }
}

// MARK: - Media

/// Video and audio. Wraps AVPlayerView, but checks first whether AVFoundation
/// can actually decode the file — otherwise it renders a crossed-out play
/// button with no explanation, which is indistinguishable from a broken app.
///
/// The usual culprit is the codec, not the container: a `.mp4` downloaded from
/// the web is often VP9 or AV1, which AVFoundation won't decode even though
/// the extension looks ordinary. ffmpeg handles those, so we offer a
/// one-click transcode instead of a dead end.
struct MediaPreview: View {
    let url: URL
    var onConvert: ((URL) -> Void)?

    @State private var state: LoadState = .checking

    enum LoadState: Equatable {
        case checking
        case playable
        case failed(reason: String, codecs: [String])
    }

    var body: some View {
        Group {
            switch state {
            case .checking:
                VStack(spacing: 10) {
                    ProgressView()
                    Text("Opening media…")
                        .font(.app(12)).foregroundStyle(Palette.textSecondary)
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity)

            case .playable:
                PlayerSurface(url: url) { message in
                    // Playback can still fail after the asset says it's fine;
                    // surface that instead of a silent crossed-out button.
                    state = .failed(
                        reason: "Playback failed: \(message). ffmpeg can usually "
                            + "transcode this into something macOS plays.",
                        codecs: [])
                }

            case .failed(let reason, let codecs):
                MediaFailureNotice(url: url, reason: reason, codecs: codecs,
                                   onConvert: onConvert)
            }
        }
        .task(id: url) { await inspect() }
    }

    /// Interrogates the asset before handing it to a player, so we can explain
    /// a failure rather than show a silent broken control.
    private func inspect() async {
        state = .checking

        guard FileManager.default.isReadableFile(atPath: url.path) else {
            state = .failed(reason: "The file can't be read. macOS may be blocking "
                            + "access to this folder — check Privacy & Security → "
                            + "Files and Folders.", codecs: [])
            return
        }

        let asset = AVURLAsset(url: url)
        do {
            let playable = try await asset.load(.isPlayable)
            let tracks = try await asset.load(.tracks)

            var codecs: [String] = []
            for track in tracks {
                let formats = try await track.load(.formatDescriptions)
                for format in formats {
                    let raw = CMFormatDescriptionGetMediaSubType(format)
                    let tag = String(format: "%c%c%c%c",
                                     (raw >> 24) & 255, (raw >> 16) & 255,
                                     (raw >> 8) & 255, raw & 255)
                        .trimmingCharacters(in: .whitespaces)
                    codecs.append(tag)
                }
            }

            if playable && !tracks.isEmpty {
                state = .playable
            } else if tracks.isEmpty {
                state = .failed(reason: "No audio or video tracks were found — the "
                                + "file may be incomplete or still downloading.",
                                codecs: codecs)
            } else {
                state = .failed(reason: "macOS can't decode this file's codec.",
                                codecs: codecs)
            }
        } catch {
            // AVFoundation's message is often just "Cannot Open", which tells
            // the user nothing actionable.
            state = .failed(
                reason: "\(error.localizedDescription). The file may be corrupt, "
                    + "still downloading, or use a codec macOS can't decode "
                    + "(VP9 and AV1 are common in web downloads).",
                codecs: [])
        }
    }
}

/// The actual player, only built once we know the asset plays.
private struct PlayerSurface: NSViewRepresentable {
    let url: URL
    var onFailure: (String) -> Void

    func makeNSView(context: Context) -> AVPlayerView {
        let view = AVPlayerView()
        view.controlsStyle = .inline
        view.showsFullScreenToggleButton = true
        view.videoGravity = .resizeAspect
        return view
    }

    func updateNSView(_ view: AVPlayerView, context: Context) {
        guard context.coordinator.loaded != url else { return }
        context.coordinator.loaded = url
        view.player?.pause()

        let item = AVPlayerItem(url: url)
        context.coordinator.watch(item, onFailure: onFailure)
        view.player = AVPlayer(playerItem: item)
    }

    func makeCoordinator() -> Coordinator { Coordinator() }

    final class Coordinator {
        var loaded: URL?
        private var observation: NSKeyValueObservation?

        /// Watches the item so a decode failure becomes a message rather than
        /// an inert control.
        func watch(_ item: AVPlayerItem, onFailure: @escaping (String) -> Void) {
            observation?.invalidate()
            observation = item.observe(\.status, options: [.new]) { item, _ in
                guard item.status == .failed else { return }
                let message = item.error?.localizedDescription ?? "unknown error"
                Task { @MainActor in onFailure(message) }
            }
        }

        deinit { observation?.invalidate() }
    }
}

private struct MediaFailureNotice: View {
    let url: URL
    let reason: String
    let codecs: [String]
    var onConvert: ((URL) -> Void)?

    var body: some View {
        VStack(spacing: 14) {
            Image(systemName: "play.slash")
                .font(.system(size: 42))
                .foregroundStyle(Palette.textSecondary.opacity(0.7))
            Text(url.lastPathComponent)
                .font(.app(15, .semibold))
                .foregroundStyle(Palette.text)
                .lineLimit(2)
                .multilineTextAlignment(.center)
            Text(reason)
                .font(.app(12.5))
                .foregroundStyle(Palette.textSecondary)
                .multilineTextAlignment(.center)
                .fixedSize(horizontal: false, vertical: true)

            if !codecs.isEmpty {
                Text("Codecs found: \(codecs.joined(separator: ", "))")
                    .font(.system(size: 11, design: .monospaced))
                    .foregroundStyle(Palette.textSecondary)
            }

            HStack(spacing: 9) {
                if let onConvert {
                    PillButton("Convert to H.264", icon: "arrow.triangle.2.circlepath",
                               style: .filled) { onConvert(url) }
                }
                PillButton("Open in Default App", icon: "arrow.up.forward.app",
                           style: .outlined) {
                    NSWorkspace.shared.open(url)
                }
            }
        }
        .padding(34)
        .frame(maxWidth: 460)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}

// MARK: - Image

struct ImagePreview: View {
    let url: URL
    @State private var zoom: CGFloat = 1

    private var image: NSImage? { NSImage(contentsOf: url) }

    var body: some View {
        VStack(spacing: 0) {
            if let image {
                ScrollView([.horizontal, .vertical]) {
                    Image(nsImage: image)
                        .resizable()
                        .interpolation(.high)
                        .aspectRatio(contentMode: .fit)
                        .scaleEffect(zoom)
                        .frame(minWidth: 200, minHeight: 200)
                        .padding(20)
                }
                HStack(spacing: 10) {
                    Text("\(Int(image.size.width)) × \(Int(image.size.height)) px")
                        .font(.app(11))
                        .foregroundStyle(Palette.textSecondary)
                    Spacer()
                    IconButton(system: "minus.magnifyingglass", size: 26) {
                        zoom = max(0.1, zoom - 0.25)
                    }
                    Text("\(Int(zoom * 100))%")
                        .font(.app(11)).monospacedDigit()
                        .foregroundStyle(Palette.textSecondary)
                        .frame(width: 42)
                    IconButton(system: "plus.magnifyingglass", size: 26) {
                        zoom = min(8, zoom + 0.25)
                    }
                    PillButton("Fit", style: .ghost, size: .small) { zoom = 1 }
                }
                .padding(.horizontal, 14)
                .padding(.vertical, 8)
                .background(Palette.tint)
            } else {
                UnsupportedNotice(url: url, reason: "This image couldn't be decoded.")
            }
        }
    }
}

// MARK: - Text

struct TextPreview: View {
    let url: URL
    @State private var content: String?
    @State private var wrapped = false

    var body: some View {
        VStack(spacing: 0) {
            if let content {
                ScrollView([.vertical, wrapped ? [] : .horizontal]) {
                    Text(content)
                        .font(.system(size: 12, design: .monospaced))
                        .foregroundStyle(Palette.text)
                        .textSelection(.enabled)
                        .frame(maxWidth: wrapped ? .infinity : nil, alignment: .leading)
                        .padding(16)
                }
                HStack {
                    Text("\(content.components(separatedBy: .newlines).count) lines")
                        .font(.app(11))
                        .foregroundStyle(Palette.textSecondary)
                    Spacer()
                    Toggle("Wrap", isOn: $wrapped).toggleStyle(.pill).fixedSize()
                    CopyButton(text: content)
                }
                .padding(.horizontal, 14)
                .padding(.vertical, 8)
                .background(Palette.tint)
            } else {
                ProgressView().frame(maxWidth: .infinity, maxHeight: .infinity)
            }
        }
        .task(id: url) {
            // Reading happens off the main thread; a big log shouldn't freeze
            // the window.
            content = await Task.detached {
                (try? String(contentsOf: url, encoding: .utf8))
                    ?? (try? String(contentsOf: url, encoding: .isoLatin1))
                    ?? "(This file isn't readable as text.)"
            }.value
        }
    }
}

// MARK: - Converted mesh

/// Shows a mesh format SceneKit can't read by converting it to OBJ in a temp
/// directory first. This is what lets an FEA `.msh` or `.vtu` be inspected
/// visually instead of showing a placeholder icon.
struct ConvertedMeshPreview: View {
    let url: URL
    @State private var converted: URL?
    @State private var failure: String?

    var body: some View {
        Group {
            if let converted {
                ModelPreview(url: converted)
            } else if let failure {
                UnsupportedNotice(url: url, reason: failure)
            } else {
                VStack(spacing: 12) {
                    ProgressView()
                    Text("Preparing 3D preview…")
                        .font(.app(12))
                        .foregroundStyle(Palette.textSecondary)
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity)
            }
        }
        .task(id: url) { await prepare() }
    }

    private func prepare() async {
        converted = nil
        failure = nil
        let temp = FileManager.default.temporaryDirectory
            .appendingPathComponent("krackn-preview-\(UUID().uuidString)", isDirectory: true)
        try? FileManager.default.createDirectory(at: temp, withIntermediateDirectories: true)
        do {
            let output = try await ConversionEngine.convert(
                source: url,
                to: ConversionFormat(ext: "obj", label: "OBJ", backend: .meshio),
                outputDirectory: temp, quality: 100)
            converted = output
        } catch {
            failure = "Previewing this mesh needs the meshio engine. "
                + "Open Utilities → Engines and press Set Up All Tools."
        }
    }
}

// MARK: - Fallback notice

struct UnsupportedNotice: View {
    let url: URL
    let reason: String

    var body: some View {
        VStack(spacing: 14) {
            Image(systemName: "doc.questionmark")
                .font(.system(size: 44))
                .foregroundStyle(Palette.textSecondary.opacity(0.7))
            Text(url.lastPathComponent)
                .font(.app(15, .semibold))
                .foregroundStyle(Palette.text)
            Text(reason)
                .font(.app(12.5))
                .foregroundStyle(Palette.textSecondary)
                .multilineTextAlignment(.center)
            PillButton("Open in Default App", icon: "arrow.up.forward.app", style: .outlined) {
                NSWorkspace.shared.open(url)
            }
        }
        .padding(30)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}
