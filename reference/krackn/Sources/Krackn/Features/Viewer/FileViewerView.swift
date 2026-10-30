import SwiftUI
import Quartz
import UniformTypeIdentifiers

/// Wraps AppKit's QuickLook preview. This is the same engine Finder uses for the
/// spacebar preview, so it renders an enormous range of types — images, PDFs,
/// video, audio, Office and iWork documents, source code, archives — without us
/// writing a renderer per format.
struct QuickLookView: NSViewRepresentable {
    let url: URL

    /// URL bridges to QLPreviewItem, but wrapping it in a concrete object is the
    /// reliable way to feed the preview view across macOS versions.
    final class Item: NSObject, QLPreviewItem {
        let previewItemURL: URL?
        init(_ url: URL) { previewItemURL = url }
    }

    func makeNSView(context: Context) -> QLPreviewView {
        let view = QLPreviewView(frame: .zero, style: .normal) ?? QLPreviewView()
        view.autostarts = true
        view.previewItem = Item(url)
        return view
    }

    func updateNSView(_ view: QLPreviewView, context: Context) {
        // Only refresh when the file actually changed, or QuickLook flickers
        // and restarts media on every SwiftUI update.
        if (view.previewItem as? Item)?.previewItemURL != url {
            view.previewItem = Item(url)
            view.refreshPreviewItem()
        }
    }
}

@MainActor
final class FileViewerModel: ObservableObject {
    @Published var current: URL?
    @Published var recents: [URL] = []

    func open(_ url: URL) {
        current = url
        recents.removeAll { $0 == url }
        recents.insert(url, at: 0)
        recents = Array(recents.prefix(10))
    }

    func close() { current = nil }

    /// Back to the empty state, keeping the recents list.
    func reset() { current = nil }

    // MARK: - File facts for the info bar

    func size(of url: URL) -> String {
        let bytes = (try? url.resourceValues(forKeys: [.fileSizeKey]).fileSize) ?? 0
        return ByteCountFormatter.string(fromByteCount: Int64(bytes), countStyle: .file)
    }

    func kind(of url: URL) -> String {
        if let type = try? url.resourceValues(forKeys: [.contentTypeKey]).contentType {
            return type.localizedDescription ?? type.identifier
        }
        return url.pathExtension.uppercased()
    }

    /// Whether the Convert tab can take this file, so we can offer a shortcut.
    func isConvertible(_ url: URL) -> Bool {
        ConversionEngine.category(for: url) != nil
    }
}

struct FileViewerView: View {
    /// Owned by RootView so recents and the open file survive navigation —
    /// a `@StateObject` here would be discarded on every tab switch.
    @ObservedObject var model: FileViewerModel
    @Environment(\.inFloatingPanel) private var inPanel
    @Environment(\.hostedUnderBar) private var underBar
    @State private var isTargeted = false

    /// A file handed over from a dock drop. Consumed once, then cleared.
    var pendingFile: Binding<URL?> = .constant(nil)
    /// Set by the app so the "Convert this" button can jump tabs.
    var onConvert: ((URL) -> Void)?

    var body: some View {
        VStack(spacing: 0) {
            if let url = model.current {
                infoBar(url)
                Rectangle().fill(Palette.separator).frame(height: 1)
                previewBody(for: url)
                    .background(Palette.background)
            } else {
                emptyState
            }
        }
        .onDrop(of: DropRouter.acceptedTypes, isTargeted: $isTargeted) { providers in
            handleDrop(providers)
        }
        .onAppear { consumePending() }
        .onChange(of: pendingFile.wrappedValue) { _, _ in consumePending() }
        .overlay {
            if isTargeted {
                RoundedRectangle(cornerRadius: 18)
                    .strokeBorder(Palette.accent, style: StrokeStyle(lineWidth: 1.5, dash: [8, 6]))
                    .padding(10)
                    .allowsHitTesting(false)
            }
        }
    }

    /// Picks the right renderer for the file. Meshes SceneKit can't read are
    /// converted to a temporary OBJ first, so an FEA .msh still shows geometry
    /// rather than a generic icon.
    @ViewBuilder
    private func previewBody(for url: URL) -> some View {
        let ext = url.pathExtension.lowercased()

        if PreviewKind.convertibleMeshes.contains(ext) {
            ConvertedMeshPreview(url: url).id(url)
        } else {
            switch PreviewKind.forFile(url) {
            case .model3D: ModelPreview(url: url).id(url)
            case .media: MediaPreview(url: url, onConvert: onConvert).id(url)
            case .image: ImagePreview(url: url).id(url)
            case .text: TextPreview(url: url).id(url)
            case .quickLook: QuickLookView(url: url).id(url)
            }
        }
    }

    // MARK: - Info bar

    private func infoBar(_ url: URL) -> some View {
        HStack(spacing: 12) {
            // Drag handle: carries the real file URL, so dropping it on a web
            // page's upload field, in Finder, on a browser tab, or on another
            // of this app's tools all work. The real Finder icon rather than a
            // category glyph, so the file is recognisable at a glance.
            FileIcon(url: url, size: 26)
                .frame(width: 28)
                .contentShape(Rectangle())
                .onDrag { NSItemProvider.file(url) }
                .help("Drag into a page, Finder or another tool")

            VStack(alignment: .leading, spacing: 1) {
                Text(url.lastPathComponent)
                    .font(.app(13.5, .medium))
                    .foregroundStyle(Palette.text)
                    .lineLimit(1)
                    .truncationMode(.middle)
                Text("\(model.kind(of: url)) · \(model.size(of: url))")
                    .font(.app(11))
                    .foregroundStyle(Palette.textSecondary)
            }
            .contentShape(Rectangle())
            .onDrag { NSItemProvider.file(url) }

            Spacer(minLength: 12)

            if model.isConvertible(url), onConvert != nil {
                PillButton("Convert", icon: "arrow.triangle.2.circlepath",
                           style: .outlined, size: .small) {
                    onConvert?(url)
                }
            }
            PillButton("Open", icon: "arrow.up.forward.app", style: .outlined, size: .small) {
                NSWorkspace.shared.open(url)
            }
            IconButton(system: "folder", size: 30) {
                NSWorkspace.shared.activateFileViewerSelecting([url])
            }
            .help("Reveal in Finder")
            IconButton(system: "xmark", size: 30) { model.close() }
                .help("Close")
        }
        .padding(.horizontal, inPanel ? 11 : 16)
        .padding(.top, inPanel || underBar ? 8 : Self.trafficLightInset + 2)
        .padding(.bottom, 10)
    }

    // MARK: - Empty state

    private var emptyState: some View {
        ToolEmptyState(
            icon: "doc.viewfinder",
            title: "Open any file",
            message: "Drop a file here, or choose one.",
            isTargeted: isTargeted,
            recents: model.recents,
            onSelectRecent: { model.open($0) }
        ) {
            PillButton("Choose File", icon: "folder", style: .filled) { pick() }
        }
    }

    // MARK: - Helpers

    private func pick() {
        let panel = NSOpenPanel()
        panel.allowsMultipleSelection = false
        panel.canChooseDirectories = false
        if panel.runModal() == .OK, let url = panel.url { model.open(url) }
    }

    private func consumePending() {
        guard let file = pendingFile.wrappedValue else { return }
        model.open(file)
        pendingFile.wrappedValue = nil
    }

    /// Accepts Finder files, raw image bytes, and web URLs alike — see
    /// DropRouter for why each needs different handling.
    private func handleDrop(_ providers: [NSItemProvider]) -> Bool {
        Task {
            let urls = await DropRouter.resolve(providers)
            guard let first = urls.first else { return }
            await MainActor.run { model.open(first) }
        }
        return true
    }
}

