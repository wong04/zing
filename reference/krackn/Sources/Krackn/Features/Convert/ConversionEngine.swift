import Foundation
import PDFKit
import AppKit

/// Which backend handles a given conversion. Three of the four ship with macOS,
/// which is why this works out of the box for most everyday files.
enum ConversionBackend: String {
    case sips = "sips"              // images — built in
    case textutil = "textutil"      // rich text / word docs — built in
    case pdfkit = "PDFKit"          // PDF assembly and page export — built in
    case archive = "ditto"          // zip/tar — built in
    case ffmpeg = "ffmpeg"          // audio and video
    case imagemagick = "magick"     // extended image formats
    case pandoc = "pandoc"          // markup and publishing formats
    case meshio = "meshio"          // 3D and FEA meshes

    var isBuiltIn: Bool {
        switch self {
        case .sips, .textutil, .pdfkit, .archive: return true
        case .ffmpeg, .imagemagick, .pandoc, .meshio: return false
        }
    }

    /// The tool the app installs automatically for this backend.
    var managedTool: ManagedTool? {
        switch self {
        case .ffmpeg: return .ffmpeg
        case .imagemagick: return .imagemagick
        case .pandoc: return .pandoc
        case .meshio: return .meshio
        default: return nil
        }
    }
}

struct ConversionFormat: Identifiable, Hashable {
    let ext: String
    let label: String
    let backend: ConversionBackend

    var id: String { ext }
}

enum ConversionCategory: String, CaseIterable, Identifiable {
    case image = "Image"
    case document = "Document"
    case audio = "Audio"
    case video = "Video"
    case model = "3D / Mesh"
    case archive = "Archive"

    var id: String { rawValue }

    var icon: String {
        switch self {
        case .image: return "photo"
        case .document: return "doc.text"
        case .audio: return "waveform"
        case .video: return "film"
        case .model: return "cube.transparent"
        case .archive: return "archivebox"
        }
    }

    /// Extensions we'll accept as input for this category, drawn from the
    /// shared catalog so inputs and outputs never drift apart.
    var inputExtensions: [String] {
        switch self {
        case .image: return FormatCatalog.sipsImages + FormatCatalog.magickImages + ["pdf"]
        case .document: return FormatCatalog.documents
        case .audio: return FormatCatalog.audio + ["mp4", "mov", "mkv"]
        case .video: return FormatCatalog.video
        case .model: return FormatCatalog.meshes + FormatCatalog.modelInterchange
        case .archive: return FormatCatalog.archives
        }
    }

    var outputFormats: [ConversionFormat] {
        switch self {
        case .image:
            // sips ships with macOS; the ImageMagick ones light up once the
            // app has installed it.
            return FormatCatalog.sipsImages.map {
                ConversionFormat(ext: $0, label: FormatCatalog.label($0), backend: .sips)
            } + FormatCatalog.magickImages.map {
                ConversionFormat(ext: $0, label: FormatCatalog.label($0), backend: .imagemagick)
            } + [ConversionFormat(ext: "pdf", label: "PDF (combined)", backend: .pdfkit)]

        case .document:
            return [
                ConversionFormat(ext: "pdf", label: "PDF", backend: .pdfkit),
                ConversionFormat(ext: "txt", label: "Plain text", backend: .textutil),
                ConversionFormat(ext: "rtf", label: "RTF", backend: .textutil),
                ConversionFormat(ext: "html", label: "HTML", backend: .textutil),
                ConversionFormat(ext: "docx", label: "Word (.docx)", backend: .textutil),
            ] + ["md", "epub", "tex", "rst", "odt", "org", "asciidoc", "docbook",
                 "opml", "fb2", "man", "csv", "json", "xml"].map {
                ConversionFormat(ext: $0, label: FormatCatalog.label($0), backend: .pandoc)
            }

        case .audio:
            return FormatCatalog.audio.map {
                ConversionFormat(ext: $0, label: FormatCatalog.label($0), backend: .ffmpeg)
            }

        case .video:
            return FormatCatalog.video.map {
                ConversionFormat(ext: $0, label: FormatCatalog.label($0), backend: .ffmpeg)
            }

        case .model:
            return FormatCatalog.meshes.map {
                ConversionFormat(ext: $0, label: FormatCatalog.label($0), backend: .meshio)
            }

        case .archive:
            return ["zip", "tar", "gz"].map {
                ConversionFormat(ext: $0, label: FormatCatalog.label($0), backend: .archive)
            }
        }
    }
}

enum ConversionError: LocalizedError {
    case unreadable(String)
    case pdfBuildFailed
    case noPages
    case backendMissing(ConversionBackend)

    var errorDescription: String? {
        switch self {
        case .unreadable(let name): return "Couldn't read \(name)."
        case .pdfBuildFailed: return "Couldn't assemble the PDF."
        case .noPages: return "No pages found in the source PDF."
        case .backendMissing(let backend):
            return "\(backend.rawValue) isn't set up yet — open Convert and press "
                + "Set Up Tools, and the app will install it for you."
        }
    }
}

struct ConversionOutcome {
    let source: URL
    let destination: URL?
    let error: String?
    var succeeded: Bool { destination != nil }
}

enum ConversionEngine {

    /// Converts one file. `quality` is 0–100 and only affects lossy targets.
    static func convert(
        source: URL,
        to format: ConversionFormat,
        outputDirectory: URL,
        quality: Int,
        onProgress: (@Sendable (String) -> Void)? = nil
    ) async throws -> URL {

        let base = source.deletingPathExtension().lastPathComponent
        var destination = outputDirectory.appendingPathComponent("\(base).\(format.ext)")
        destination = uniqueURL(destination)

        // When the user has turned numbering off, clear the way explicitly:
        // several backends (`copyItem` for the PDF pass-through, `ditto` for
        // archives) refuse to write over an existing file rather than
        // replacing it, so "overwrite" would otherwise just fail.
        if !AppSettings.preserveOriginals,
           FileManager.default.fileExists(atPath: destination.path) {
            try? FileManager.default.removeItem(at: destination)
        }

        switch format.backend {
        case .sips:
            try await convertImage(source: source, destination: destination,
                                   format: format, quality: quality)
        case .textutil:
            try await convertDocument(source: source, destination: destination, format: format)
        case .pdfkit:
            try convertToPDF(source: source, destination: destination)
        case .ffmpeg:
            try await convertMedia(source: source, destination: destination,
                                   format: format, quality: quality, onProgress: onProgress)
        case .imagemagick:
            try await convertWithMagick(source: source, destination: destination,
                                        quality: quality)
        case .pandoc:
            try await convertWithPandoc(source: source, destination: destination,
                                        format: format)
        case .meshio:
            try await convertMesh(source: source, destination: destination)
        case .archive:
            try await makeArchive(source: source, destination: destination, format: format)
        }

        guard FileManager.default.fileExists(atPath: destination.path) else {
            throw ConversionError.unreadable(destination.lastPathComponent)
        }
        return destination
    }

    // MARK: - Images

    private static func convertImage(
        source: URL, destination: URL, format: ConversionFormat, quality: Int
    ) async throws {
        var arguments = [
            "--setProperty", "format", format.ext,
            source.path,
            "--out", destination.path,
        ]
        // sips expresses JPEG quality as a named bucket, not a number.
        if format.ext == "jpeg" {
            let bucket: String
            switch quality {
            case ..<35: bucket = "low"
            case ..<65: bucket = "normal"
            case ..<90: bucket = "high"
            default: bucket = "best"
            }
            arguments.append(contentsOf: ["--setProperty", "formatOptions", bucket])
        }

        let result = try await ShellRunner.run("sips", arguments)
        guard result.ok else {
            throw ConversionError.unreadable(
                "\(source.lastPathComponent) — \(result.stderr.trimmingCharacters(in: .whitespacesAndNewlines))")
        }
    }

    // MARK: - Documents

    private static func convertDocument(
        source: URL, destination: URL, format: ConversionFormat
    ) async throws {
        // textutil doesn't know markdown; treat it as plain text going in.
        let result = try await ShellRunner.run("textutil", [
            "-convert", format.ext,
            "-output", destination.path,
            source.path,
        ])
        guard result.ok else {
            throw ConversionError.unreadable(
                "\(source.lastPathComponent) — \(result.stderr.trimmingCharacters(in: .whitespacesAndNewlines))")
        }
    }

    // MARK: - PDF

    /// Images become a single-page PDF; an existing PDF is passed through.
    /// Text documents are routed through an attributed string so we get real
    /// pagination rather than one absurdly tall page.
    private static func convertToPDF(source: URL, destination: URL) throws {
        let ext = source.pathExtension.lowercased()

        if ext == "pdf" {
            try FileManager.default.copyItem(at: source, to: destination)
            return
        }

        if let image = NSImage(contentsOf: source) {
            let document = PDFDocument()
            guard let page = PDFPage(image: image) else { throw ConversionError.pdfBuildFailed }
            document.insert(page, at: 0)
            guard document.write(to: destination) else { throw ConversionError.pdfBuildFailed }
            return
        }

        // Fall back to the text path — this covers docx, rtf, html and txt.
        // Let AppKit sniff the format rather than forcing one: pinning it to
        // .plain would render a .docx as its raw XML.
        let attributed: NSAttributedString
        if let detected = try? NSAttributedString(
            url: source, options: [:], documentAttributes: nil
        ) {
            attributed = detected
        } else if let text = try? String(contentsOf: source, encoding: .utf8) {
            attributed = NSAttributedString(
                string: text,
                attributes: [.font: NSFont.systemFont(ofSize: 11)]
            )
        } else {
            throw ConversionError.unreadable(source.lastPathComponent)
        }

        let pageSize = NSSize(width: 612, height: 792)  // US Letter at 72dpi
        let margin: CGFloat = 48
        let textRect = NSRect(
            x: margin, y: margin,
            width: pageSize.width - margin * 2,
            height: pageSize.height - margin * 2
        )

        let data = NSMutableData()
        guard let consumer = CGDataConsumer(data: data) else {
            throw ConversionError.pdfBuildFailed
        }
        var mediaBox = CGRect(origin: .zero, size: pageSize)
        guard let pdfContext = CGContext(consumer: consumer, mediaBox: &mediaBox, nil) else {
            throw ConversionError.pdfBuildFailed
        }

        let framesetter = CTFramesetterCreateWithAttributedString(attributed as CFAttributedString)
        var location = 0
        let total = attributed.length

        while location < total {
            pdfContext.beginPDFPage(nil)
            let path = CGPath(rect: textRect, transform: nil)
            let frame = CTFramesetterCreateFrame(
                framesetter, CFRangeMake(location, 0), path, nil)
            CTFrameDraw(frame, pdfContext)
            let visible = CTFrameGetVisibleStringRange(frame)
            pdfContext.endPDFPage()

            // A zero-length page means nothing more will fit — bail rather
            // than loop forever on content that can't be laid out.
            if visible.length == 0 { break }
            location += visible.length
        }
        pdfContext.closePDF()

        try data.write(to: destination)
    }

    // MARK: - Media

    private static func convertMedia(
        source: URL, destination: URL, format: ConversionFormat,
        quality: Int, onProgress: (@Sendable (String) -> Void)?
    ) async throws {
        guard ShellRunner.which("ffmpeg") != nil else {
            throw ConversionError.backendMissing(.ffmpeg)
        }

        var arguments = ["-y", "-i", source.path]
        let ext = format.ext

        // Route by what the *target* actually is. An earlier version switched
        // on a hardcoded list of extensions, so any audio format not named
        // there (opus, aiff, wv…) fell through to the H.264 video branch and
        // produced an empty file.
        let isAudioTarget = FormatCatalog.audio.contains(ext)

        if isAudioTarget {
            arguments.append("-vn")   // never carry a video stream into audio
            switch ext {
            case "mp3":
                // libmp3lame's VBR scale is inverted: 0 is best, 9 worst.
                arguments += ["-c:a", "libmp3lame",
                              "-q:a", String(max(0, min(9, 9 - (quality * 9 / 100))))]
            case "m4a", "aac":
                arguments += ["-c:a", "aac", "-b:a", "\(max(64, quality * 3))k"]
            case "wav":
                arguments += ["-c:a", "pcm_s16le"]
            case "flac":
                arguments += ["-c:a", "flac"]
            case "alac":
                arguments += ["-c:a", "alac"]
            case "ogg":
                arguments += ["-c:a", "libvorbis",
                              "-q:a", String(max(0, min(10, quality / 10)))]
            case "opus":
                arguments += ["-c:a", "libopus", "-b:a", "\(max(32, quality * 2))k"]
            case "aiff", "aif":
                arguments += ["-c:a", "pcm_s16be"]
            default:
                // Let ffmpeg pick the container's default encoder. This is what
                // makes the long tail of formats work without a case each.
                break
            }
        } else {
            switch ext {
            case "gif":
                // Two-pass palette generation in one filtergraph — without this
                // ffmpeg's default 216-colour palette looks awful.
                arguments += [
                    "-vf", "fps=12,scale=640:-1:flags=lanczos,split[a][b];"
                        + "[a]palettegen[p];[b][p]paletteuse",
                    "-loop", "0",
                ]
            case "webm":
                arguments += ["-c:v", "libvpx-vp9",
                              "-crf", String(63 - (quality * 63 / 100)),
                              "-b:v", "0", "-c:a", "libopus"]
            case "prores":
                // ProRes lives in a .mov container.
                arguments += ["-c:v", "prores_ks", "-profile:v", "3", "-c:a", "pcm_s16le"]
            case "mp4", "mov", "mkv", "m4v", "avi", "flv", "ts", "3gp":
                // H.264 CRF runs 0 (lossless) to 51 (worst).
                arguments += ["-c:v", "libx264",
                              "-crf", String(max(0, min(51, 51 - (quality * 51 / 100)))),
                              "-preset", "medium", "-c:a", "aac"]
            default:
                break   // container defaults
            }
        }

        // ProRes has no .prores extension; correct the output path.
        let finalDestination = ext == "prores"
            ? destination.deletingPathExtension().appendingPathExtension("mov")
            : destination
        arguments.append(finalDestination.path)

        let result = try await ShellRunner.run("ffmpeg", arguments, onOutputLine: onProgress)
        guard result.ok else {
            throw ConversionError.unreadable(
                "\(source.lastPathComponent) — \(explainFFmpegFailure(result.stderr, target: ext))")
        }
    }

    /// ffmpeg's raw stderr is noisy and its last line is often just
    /// "Conversion failed!". Translate the common causes into something a user
    /// can act on.
    private static func explainFFmpegFailure(_ stderr: String, target: String) -> String {
        if stderr.contains("does not contain any stream")
            || stderr.contains("Output file does not contain") {
            return FormatCatalog.audio.contains(target)
                ? "this file has no audio track to extract"
                : "nothing to convert into that format"
        }
        if stderr.contains("Unknown encoder") || stderr.contains("Encoder not found") {
            return "this build of FFmpeg doesn't include that codec"
        }
        if stderr.contains("Invalid data found") {
            return "the file appears to be corrupt or isn't the format its name claims"
        }
        if stderr.contains("No such file") { return "the source file is missing" }
        return lastLine(stderr)
    }

    // MARK: - ImageMagick

    /// Extended image formats: vector (SVG), modern codecs (AVIF, JXL, WebP),
    /// camera RAW, and layered PSD — everything `sips` won't touch.
    private static func convertWithMagick(
        source: URL, destination: URL, quality: Int
    ) async throws {
        // ImageMagick 7 uses `magick`; older installs expose `convert`.
        let tool = ShellRunner.which("magick") != nil ? "magick" : "convert"
        guard ShellRunner.which(tool) != nil else {
            throw ConversionError.backendMissing(.imagemagick)
        }
        var arguments = [source.path]
        // Rasterising a vector at its tiny default density looks broken, so
        // render SVGs at print resolution.
        if source.pathExtension.lowercased() == "svg" {
            arguments = ["-density", "300"] + arguments
        }
        arguments += ["-quality", String(max(1, min(100, quality))), destination.path]

        let result = try await ShellRunner.run(tool, arguments)
        guard result.ok else {
            throw ConversionError.unreadable(
                "\(source.lastPathComponent) — \(lastLine(result.stderr))")
        }
    }

    // MARK: - Pandoc

    /// Markup and publishing formats: Markdown, LaTeX, ePub, reStructuredText,
    /// Org, AsciiDoc, DocBook and friends.
    private static func convertWithPandoc(
        source: URL, destination: URL, format: ConversionFormat
    ) async throws {
        guard ShellRunner.which("pandoc") != nil else {
            throw ConversionError.backendMissing(.pandoc)
        }
        var arguments = [source.path, "-o", destination.path]
        // Pandoc can't infer a standalone document for these from the
        // extension alone.
        if ["epub", "tex", "html", "docx", "odt"].contains(format.ext) {
            arguments.append("--standalone")
        }
        // PDF output needs a LaTeX engine, which most people won't have;
        // route through the built-in PDF path instead of failing.
        if format.ext == "pdf" {
            try convertToPDF(source: source, destination: destination)
            return
        }
        let result = try await ShellRunner.run("pandoc", arguments)
        guard result.ok else {
            throw ConversionError.unreadable(
                "\(source.lastPathComponent) — \(lastLine(result.stderr))")
        }
    }

    // MARK: - Meshes

    /// 3D and finite-element meshes via meshio: STL, MSH, VTK/VTU, OBJ, PLY,
    /// XDMF, Abaqus INP, Nastran BDF and more. This is the STL → MSH path.
    private static func convertMesh(source: URL, destination: URL) async throws {
        // Prefer the app's own virtualenv, where meshio actually lives.
        guard let python = await ToolInstaller.pythonPath else {
            throw ConversionError.backendMissing(.meshio)
        }
        // Several extensions are ambiguous — ".msh" alone means Gmsh to most
        // engineers, but meshio defaults it to Ansys Fluent. Naming the format
        // explicitly avoids silently writing the wrong one.
        let explicitFormat: String?
        switch destination.pathExtension.lowercased() {
        case "msh": explicitFormat = "gmsh"
        case "inp": explicitFormat = "abaqus"
        case "bdf", "fem", "nas": explicitFormat = "nastran"
        case "e", "exo": explicitFormat = "exodus"
        case "dat": explicitFormat = "tecplot"
        default: explicitFormat = nil
        }

        let script = """
        import sys, meshio
        mesh = meshio.read(sys.argv[1])
        fmt = sys.argv[3] if len(sys.argv) > 3 and sys.argv[3] else None
        if fmt:
            meshio.write(sys.argv[2], mesh, file_format=fmt)
        else:
            meshio.write(sys.argv[2], mesh)
        """
        let result = try await ShellRunner.run(
            python, ["-c", script, source.path, destination.path, explicitFormat ?? ""])
        guard result.ok else {
            let message = lastLine(result.stderr)
            if message.contains("No module named") {
                throw ConversionError.backendMissing(.meshio)
            }
            throw ConversionError.unreadable("\(source.lastPathComponent) — \(message)")
        }
    }

    // MARK: - Archives

    private static func makeArchive(
        source: URL, destination: URL, format: ConversionFormat
    ) async throws {
        let result: ShellResult
        switch format.ext {
        case "zip":
            // ditto keeps macOS metadata intact, unlike plain zip.
            result = try await ShellRunner.run(
                "/usr/bin/ditto", ["-c", "-k", "--sequesterRsrc", source.path, destination.path])
        case "tar":
            result = try await ShellRunner.run(
                "/usr/bin/tar",
                ["-cf", destination.path, "-C", source.deletingLastPathComponent().path,
                 source.lastPathComponent])
        default:  // gz
            result = try await ShellRunner.run(
                "/usr/bin/tar",
                ["-czf", destination.path, "-C", source.deletingLastPathComponent().path,
                 source.lastPathComponent])
        }
        guard result.ok else {
            throw ConversionError.unreadable(
                "\(source.lastPathComponent) — \(lastLine(result.stderr))")
        }
    }

    // MARK: - Helpers

    private static func lastLine(_ text: String) -> String {
        text.split(separator: "\n").last.map(String.init)
            ?? "unknown error"
    }

    /// Never silently clobber an existing file — append " 2", " 3", etc.
    ///
    /// Honours the "Never overwrite existing files" preference, which used to
    /// be stored and never read: the numbering happened unconditionally, so
    /// turning the switch off did nothing at all.
    private static func uniqueURL(_ proposed: URL) -> URL {
        guard AppSettings.preserveOriginals else { return proposed }

        var candidate = proposed
        var counter = 2
        let directory = proposed.deletingLastPathComponent()
        let base = proposed.deletingPathExtension().lastPathComponent
        let ext = proposed.pathExtension

        while FileManager.default.fileExists(atPath: candidate.path) {
            candidate = directory.appendingPathComponent("\(base) \(counter).\(ext)")
            counter += 1
        }
        return candidate
    }

    static func category(for url: URL) -> ConversionCategory? {
        FormatCatalog.category(for: url.pathExtension)
    }
}
