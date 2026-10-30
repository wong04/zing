import Foundation

/// The full set of formats the converter understands, grouped by discipline.
///
/// Coverage comes from routing to the right backend rather than implementing
/// codecs: `sips` and ImageMagick for images, FFmpeg for media, `textutil` and
/// Pandoc for documents, and meshio for 3D/FEA meshes. That's why STL → MSH
/// works alongside MP4 → GIF.
enum FormatCatalog {

    // MARK: - Images

    /// Handled by macOS's built-in `sips`. No install required.
    static let sipsImages = [
        "jpeg", "jpg", "png", "tiff", "tif", "gif", "bmp", "heic", "heif",
        "jp2", "tga", "psd", "pict", "sgi", "exr",
    ]

    /// ImageMagick adds vector, RAW and exotic formats on top of sips.
    static let magickImages = [
        "webp", "avif", "svg", "ico", "pcx", "ppm", "pgm", "pbm", "pnm", "xpm",
        "dds", "hdr", "jxl", "dpx", "fits", "miff", "pfm", "rgb", "xbm",
        // camera RAW
        "cr2", "cr3", "nef", "arw", "dng", "orf", "raf", "rw2", "pef", "srw",
    ]

    // MARK: - Audio

    static let audio = [
        "mp3", "m4a", "aac", "wav", "flac", "ogg", "opus", "aiff", "aif",
        "wma", "alac", "amr", "ac3", "dts", "caf", "au", "mka", "spx", "ape",
        "wv", "mp2", "ra", "voc", "w64",
    ]

    // MARK: - Video

    static let video = [
        "mp4", "mov", "mkv", "avi", "webm", "flv", "wmv", "m4v", "mpg", "mpeg",
        "mts", "m2ts", "ts", "vob", "ogv", "3gp", "3g2", "asf", "rm", "rmvb",
        "divx", "f4v", "mxf", "y4m", "gif", "apng", "prores",
    ]

    // MARK: - Documents

    /// Pandoc handles the long tail of markup and publishing formats.
    static let documents = [
        "pdf", "docx", "doc", "odt", "rtf", "rtfd", "txt", "md", "markdown",
        "html", "htm", "xhtml", "epub", "tex", "latex", "org", "rst",
        "wiki", "textile", "asciidoc", "adoc", "opml", "fb2", "icml",
        "docbook", "jats", "man", "ms", "csv", "tsv", "json", "yaml", "xml",
        "pptx", "key", "pages", "numbers", "xlsx", "ods", "webarchive",
    ]

    // MARK: - 3D models and meshes

    /// meshio covers simulation/FEA meshes — this is the STL ↔ MSH path.
    static let meshes = [
        "stl", "msh", "vtk", "vtu", "vtp", "xdmf", "xmf", "obj", "off", "ply",
        "mesh", "meshb", "med", "bdf", "fem", "nas", "inp", "cgns", "e", "exo",
        "ugrid", "avs", "dato", "h5m", "post", "su2", "svg3d", "tec", "wkt",
        "gmsh", "abaqus", "ansys", "dolfin", "permas", "flac3d", "mdpa", "neu",
    ]

    /// Common interchange formats for design and 3D printing. Handled by
    /// meshio where possible; a few are read-only in practice.
    static let modelInterchange = [
        "glb", "gltf", "fbx", "dae", "3ds", "3mf", "amf", "x3d", "usdz", "usd",
        "step", "stp", "iges", "igs", "brep", "wrl", "vrml",
    ]

    // MARK: - Archives / data

    static let archives = ["zip", "tar", "gz", "bz2", "xz", "7z", "rar", "zst"]

    // MARK: - Lookup

    /// Every extension the converter recognises as input.
    static var allInputs: Set<String> {
        Set(sipsImages + magickImages + audio + video + documents
            + meshes + modelInterchange + archives)
    }

    static func category(for ext: String) -> ConversionCategory? {
        let e = ext.lowercased()
        // Order matters: several extensions appear in more than one list.
        if meshes.contains(e) || modelInterchange.contains(e) { return .model }
        if video.contains(e) { return .video }
        if audio.contains(e) { return .audio }
        if sipsImages.contains(e) || magickImages.contains(e) { return .image }
        if documents.contains(e) { return .document }
        if archives.contains(e) { return .archive }
        return nil
    }

    /// Human label for a format, used in the picker.
    static func label(_ ext: String) -> String {
        switch ext {
        case "jpeg": return "JPEG"
        case "png": return "PNG"
        case "tiff": return "TIFF"
        case "heic": return "HEIC"
        case "webp": return "WebP"
        case "avif": return "AVIF"
        case "svg": return "SVG (vector)"
        case "jxl": return "JPEG XL"
        case "mp4": return "MP4 (H.264)"
        case "webm": return "WebM (VP9)"
        case "mkv": return "Matroska"
        case "gif": return "Animated GIF"
        case "prores": return "ProRes (.mov)"
        case "mp3": return "MP3"
        case "m4a": return "M4A / AAC"
        case "wav": return "WAV (lossless)"
        case "flac": return "FLAC (lossless)"
        case "opus": return "Opus"
        case "pdf": return "PDF"
        case "docx": return "Word (.docx)"
        case "epub": return "ePub"
        case "tex": return "LaTeX"
        case "md": return "Markdown"
        case "rst": return "reStructuredText"
        case "stl": return "STL (3D print)"
        case "msh": return "Gmsh MSH (FEA)"
        case "vtk": return "VTK (legacy)"
        case "vtu": return "VTU (XML)"
        case "obj": return "OBJ"
        case "ply": return "PLY"
        case "off": return "OFF"
        case "xdmf": return "XDMF"
        case "inp": return "Abaqus INP"
        case "bdf": return "Nastran BDF"
        case "med": return "MED (Salome)"
        case "cgns": return "CGNS"
        case "glb": return "glTF Binary"
        case "gltf": return "glTF"
        case "usdz": return "USDZ"
        case "3mf": return "3MF"
        default: return ext.uppercased()
        }
    }
}
