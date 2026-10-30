import SwiftUI
import UniformTypeIdentifiers

/// Turns anything dropped on the app into a local file URL.
///
/// A drag from Finder carries a file URL, but a drag out of a web page usually
/// doesn't: it may be an http(s) URL, raw image bytes, or just a promise. Each
/// of those has to be resolved — and for a web URL that means downloading it —
/// before the viewer or converter has something to work with.
extension NSItemProvider {
    /// A drag source for a file of *any* type.
    ///
    /// `NSItemProvider(contentsOf:)` registers only the file's own content
    /// type — an `.stl` advertises `public.3d-content`, a `.msh` gets a dynamic
    /// `dyn.…` identifier — and neither conforms to `public.file-url`. So a
    /// drop target declaring the usual types refused them outright, which is
    /// why images and PDFs could be dragged between the tools and meshes,
    /// video and everything exotic could not.
    ///
    /// Registering the URL *as a URL* is what makes any file draggable
    /// everywhere: our own targets, Finder, and a web page's upload field.
    static func file(_ url: URL) -> NSItemProvider {
        let provider = NSItemProvider()
        // public.file-url + public.url. The universal part.
        provider.registerObject(url as NSURL, visibility: .all)
        provider.suggestedName = url.lastPathComponent

        // Plus the real bytes under the file's own type, for apps that want
        // content rather than a reference.
        if let type = UTType(filenameExtension: url.pathExtension) ?? UTType(filenameExtension: "") {
            provider.registerFileRepresentation(
                forTypeIdentifier: type.identifier,
                fileOptions: [.openInPlace],
                visibility: .all
            ) { completion in
                completion(url, true, nil)
                return nil
            }
        }
        return provider
    }
}

extension View {
    /// Makes this view a file drop target across its whole frame.
    ///
    /// The `contentShape` is the entire point. A drop zone drawn as a dashed
    /// outline around empty space has nothing hit-testable in the middle: the
    /// fill is `.clear`, and SwiftUI won't route a drop to a clear fill. The
    /// background remover's zone was built exactly that way, so drops only
    /// registered if you happened to release the file directly over its icon,
    /// its label, or its button — which is why it read as "drag doesn't work
    /// here" while the same pattern appeared to work in the converter, where a
    /// second `onDrop` on the outer container was quietly catching everything.
    func dropsFiles(isTargeted: Binding<Bool>,
                    onDrop: @escaping ([URL]) -> Void) -> some View {
        contentShape(Rectangle())
            .onDrop(of: DropRouter.acceptedTypes, isTargeted: isTargeted) { providers in
                Task {
                    let urls = await DropRouter.resolve(providers)
                    guard !urls.isEmpty else { return }
                    await MainActor.run { onDrop(urls) }
                }
                return true
            }
    }
}

enum DropRouter {

    /// Everything a drop target accepts, in the order the system should offer
    /// it.
    ///
    /// Deliberately does **not** include `UTType.item`. `.item` is the root
    /// every type conforms to, so registering it turns a view into a universal
    /// drop target — and the tracking AppKit installs for that swallows
    /// right-clicks. Adding it to make exotic files draggable silently killed
    /// every context menu underneath a drop target: the tab strip's (and with
    /// it the only way to pin a tab) and the recents lists'.
    ///
    /// Nothing is lost by leaving it out. Files dragged from Finder *and* from
    /// this app's own tools both advertise `public.file-url` — that's what
    /// `NSItemProvider.file(_:)` guarantees, and it's what actually fixed
    /// dragging meshes and video between the tools. `.item` was never the part
    /// doing the work.
    static let acceptedTypes: [UTType] = [.fileURL, .image, .url, .text]

    /// For views that only want to *notice* a drag passing over them and never
    /// consume it — the tab strip's spring-loading.
    static let hoverTypes: [UTType] = [.fileURL, .image, .url]

    /// Resolves a drop into an on-disk file. Web content is written into a
    /// temp folder so both the viewer and converter can treat it like any
    /// other file.
    static func resolve(_ providers: [NSItemProvider]) async -> [URL] {
        var results: [URL] = []
        for provider in providers {
            if let url = await resolveOne(provider) {
                results.append(url)
            }
        }
        return results
    }

    private static func resolveOne(_ provider: NSItemProvider) async -> URL? {
        // 1. A real file on disk — the Finder case, and the cheapest.
        if provider.hasItemConformingToTypeIdentifier(UTType.fileURL.identifier),
           let url = await loadItem(provider, type: UTType.fileURL.identifier),
           let fileURL = asFileURL(url) {
            return fileURL
        }

        // 2. Raw image bytes. Safari supplies these when you drag an <img>.
        if provider.hasItemConformingToTypeIdentifier(UTType.image.identifier),
           let data = await loadData(provider, type: UTType.image.identifier),
           !data.isEmpty {
            return write(data, suggestedName: provider.suggestedName, fallbackExt: "png")
        }

        // 3. A web URL — download it. This is the common case for dragging an
        //    image out of a page that only offers a link.
        if provider.hasItemConformingToTypeIdentifier(UTType.url.identifier),
           let item = await loadItem(provider, type: UTType.url.identifier),
           let remote = asWebURL(item) {
            return await download(remote)
        }

        // 4. Anything else with a file behind it. This is what makes meshes,
        //    video, archives and unrecognised types work when they arrive from
        //    an app that doesn't advertise a file URL.
        //
        //    Text and URL types are excluded deliberately. The system will
        //    happily materialise a dragged *string* into a temp file, so
        //    without this guard dropping a text selection onto a tool would
        //    silently manufacture a junk file in Downloads. Anything that's
        //    genuinely a text file arrives with a file URL and is caught by
        //    step 1 long before this.
        for identifier in provider.registeredTypeIdentifiers {
            guard let type = UTType(identifier),
                  !type.conforms(to: .text),
                  !type.conforms(to: .url),
                  !type.conforms(to: .plainText)
            else { continue }
            if let url = await loadFile(provider, type: identifier) {
                return url
            }
        }

        return nil
    }

    /// Copies out a provider's file representation.
    ///
    /// The URL handed to the callback is only valid inside it — the system
    /// deletes the backing file as soon as it returns — so the bytes have to be
    /// copied before we can hand the path anywhere else.
    private static func loadFile(_ provider: NSItemProvider, type: String) async -> URL? {
        await withCheckedContinuation { continuation in
            _ = provider.loadFileRepresentation(forTypeIdentifier: type) { url, _ in
                guard let url else {
                    continuation.resume(returning: nil)
                    return
                }
                let destination = unique(
                    inboxDirectory.appendingPathComponent(url.lastPathComponent))
                do {
                    try FileManager.default.copyItem(at: url, to: destination)
                    continuation.resume(returning: destination)
                } catch {
                    continuation.resume(returning: nil)
                }
            }
        }
    }

    // MARK: - Provider plumbing

    private static func loadItem(_ provider: NSItemProvider, type: String) async -> Any? {
        await withCheckedContinuation { continuation in
            provider.loadItem(forTypeIdentifier: type, options: nil) { item, _ in
                continuation.resume(returning: item)
            }
        }
    }

    private static func loadData(_ provider: NSItemProvider, type: String) async -> Data? {
        await withCheckedContinuation { continuation in
            provider.loadDataRepresentation(forTypeIdentifier: type) { data, _ in
                continuation.resume(returning: data)
            }
        }
    }

    /// Providers hand back a URL as bookmark `Data`, an `NSURL`, or a string
    /// depending on the source app; accepting only one silently drops the rest.
    private static func asFileURL(_ item: Any) -> URL? {
        if let data = item as? Data,
           let url = URL(dataRepresentation: data, relativeTo: nil), url.isFileURL {
            return url
        }
        if let url = item as? URL, url.isFileURL { return url }
        if let string = item as? String, let url = URL(string: string), url.isFileURL {
            return url
        }
        return nil
    }

    private static func asWebURL(_ item: Any) -> URL? {
        let candidate: URL?
        if let data = item as? Data {
            candidate = URL(dataRepresentation: data, relativeTo: nil)
        } else if let url = item as? URL {
            candidate = url
        } else if let string = item as? String {
            candidate = URL(string: string.trimmingCharacters(in: .whitespacesAndNewlines))
        } else {
            candidate = nil
        }
        guard let candidate, let scheme = candidate.scheme?.lowercased(),
              scheme == "http" || scheme == "https" else { return nil }
        return candidate
    }

    // MARK: - Landing files

    /// Where dropped web content lands.
    ///
    /// `~/Downloads/Krackn` rather than a temp directory: a picture dragged out
    /// of a web page is a file the user now owns, and burying it somewhere
    /// under `/var/folders` meant they could see it in the app but never find
    /// it again on disk — and the system would eventually delete it.
    static var inboxDirectory: URL {
        let base = AppSettings.downloadsDirectory
            .appendingPathComponent("Krackn", isDirectory: true)
        try? FileManager.default.createDirectory(at: base, withIntermediateDirectories: true)
        return base
    }

    private static func write(_ data: Data, suggestedName: String?,
                              fallbackExt: String) -> URL? {
        var name = suggestedName ?? "dropped-\(Int(Date().timeIntervalSince1970))"
        if URL(fileURLWithPath: name).pathExtension.isEmpty {
            // Sniff the real type from the bytes rather than trusting the name.
            name += ".\(Self.detectExtension(data) ?? fallbackExt)"
        }
        let destination = unique(inboxDirectory.appendingPathComponent(name))
        do {
            try data.write(to: destination)
            return destination
        } catch {
            return nil
        }
    }

    private static func download(_ url: URL) async -> URL? {
        var request = URLRequest(url: url)
        request.timeoutInterval = 30
        guard let (data, response) = try? await URLSession.shared.data(for: request),
              !data.isEmpty else { return nil }

        var name = url.lastPathComponent
        if name.isEmpty || name == "/" { name = "download" }
        // Strip any query string that survived into the last path component.
        name = name.components(separatedBy: "?").first ?? name

        if URL(fileURLWithPath: name).pathExtension.isEmpty {
            let ext = (response.mimeType.flatMap {
                UTType(mimeType: $0)?.preferredFilenameExtension
            }) ?? detectExtension(data) ?? "bin"
            name += ".\(ext)"
        }

        let destination = unique(inboxDirectory.appendingPathComponent(name))
        do {
            try data.write(to: destination)
            return destination
        } catch {
            return nil
        }
    }

    /// Magic-number sniffing for the formats that actually turn up in drags.
    private static func detectExtension(_ data: Data) -> String? {
        let bytes = [UInt8](data.prefix(12))
        guard bytes.count >= 4 else { return nil }
        if bytes.starts(with: [0x89, 0x50, 0x4E, 0x47]) { return "png" }
        if bytes.starts(with: [0xFF, 0xD8, 0xFF]) { return "jpg" }
        if bytes.starts(with: [0x47, 0x49, 0x46]) { return "gif" }
        if bytes.starts(with: [0x25, 0x50, 0x44, 0x46]) { return "pdf" }
        if bytes.count >= 12,
           bytes[0...3] == [0x52, 0x49, 0x46, 0x46][0...3],
           bytes[8...11] == [0x57, 0x45, 0x42, 0x50][0...3] { return "webp" }
        if bytes.count >= 8, bytes[4...7] == [0x66, 0x74, 0x79, 0x70][0...3] { return "mp4" }
        return nil
    }

    private static func unique(_ proposed: URL) -> URL {
        var candidate = proposed
        var counter = 2
        let directory = proposed.deletingLastPathComponent()
        let base = proposed.deletingPathExtension().lastPathComponent
        let ext = proposed.pathExtension
        while FileManager.default.fileExists(atPath: candidate.path) {
            let name = ext.isEmpty ? "\(base) \(counter)" : "\(base) \(counter).\(ext)"
            candidate = directory.appendingPathComponent(name)
            counter += 1
        }
        return candidate
    }
}
