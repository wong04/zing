import Foundation
import Vision
import CoreImage
import AppKit

/// Cuts the subject out of a photo using Vision's foreground-instance mask —
/// the same engine behind "lift subject" in Photos and Preview.
///
/// This runs entirely on-device: no upload, no API key, no per-image cost,
/// which is the whole reason to do it here rather than call a web service.
enum BackgroundRemover {

    enum RemovalError: LocalizedError {
        case unreadable
        case noSubjectFound
        case renderFailed

        var errorDescription: String? {
            switch self {
            case .unreadable: return "That image couldn't be read."
            case .noSubjectFound:
                return "No clear subject was found. This works best on photos with a "
                    + "distinct subject — a person, animal, or object — against a "
                    + "background."
            case .renderFailed: return "The cutout couldn't be rendered."
            }
        }
    }

    /// What to put behind the subject once the original background is gone.
    enum Background: String, CaseIterable, Identifiable {
        case transparent = "Transparent"
        case white = "White"
        case black = "Black"
        case beige = "Beige"

        var id: String { rawValue }

        var color: CIColor? {
            switch self {
            case .transparent: return nil
            case .white: return CIColor(red: 1, green: 1, blue: 1)
            case .black: return CIColor(red: 0, green: 0, blue: 0)
            case .beige: return CIColor(red: 0.968, green: 0.949, blue: 0.914)
            }
        }
    }

    struct Result {
        let image: NSImage
        /// PNG bytes, so transparency survives being written to disk.
        let pngData: Data
        let subjectCount: Int
    }

    /// Removes the background from the image at `url`.
    static func removeBackground(
        from url: URL,
        background: Background = .transparent
    ) async throws -> Result {
        guard let source = CIImage(contentsOf: url) else { throw RemovalError.unreadable }
        return try await remove(from: source, background: background)
    }

    static func remove(
        from source: CIImage,
        background: Background = .transparent
    ) async throws -> Result {
        // Vision work is synchronous and CPU-heavy, so keep it off the main
        // thread — a large photo would otherwise freeze the window.
        try await Task.detached(priority: .userInitiated) {
            let request = VNGenerateForegroundInstanceMaskRequest()
            let handler = VNImageRequestHandler(ciImage: source, options: [:])
            try handler.perform([request])

            guard let observation = request.results?.first,
                  !observation.allInstances.isEmpty
            else { throw RemovalError.noSubjectFound }

            // Every detected instance at once, so a photo with two people
            // keeps both rather than only the most prominent.
            let maskBuffer = try observation.generateScaledMaskForImage(
                forInstances: observation.allInstances, from: handler)
            let mask = CIImage(cvPixelBuffer: maskBuffer)

            // The mask comes back at the analysis resolution; stretch it to the
            // original so the cutout lines up pixel for pixel.
            let scaleX = source.extent.width / mask.extent.width
            let scaleY = source.extent.height / mask.extent.height
            let scaledMask = mask.transformed(
                by: CGAffineTransform(scaleX: scaleX, y: scaleY))

            let composed: CIImage
            if let color = background.color {
                let backdrop = CIImage(color: color).cropped(to: source.extent)
                composed = source
                    .applyingFilter("CIBlendWithMask", parameters: [
                        kCIInputBackgroundImageKey: backdrop,
                        kCIInputMaskImageKey: scaledMask,
                    ])
            } else {
                // No backdrop: the mask becomes the alpha channel.
                composed = source
                    .applyingFilter("CIBlendWithMask", parameters: [
                        kCIInputMaskImageKey: scaledMask,
                    ])
            }

            let context = CIContext()
            guard let cgImage = context.createCGImage(composed, from: source.extent),
                  let png = NSBitmapImageRep(cgImage: cgImage)
                    .representation(using: .png, properties: [:])
            else { throw RemovalError.renderFailed }

            return Result(
                image: NSImage(cgImage: cgImage, size: source.extent.size),
                pngData: png,
                subjectCount: observation.allInstances.count)
        }.value
    }

    /// Writes the cutout to Downloads as `<name>-cutout.png`.
    ///
    /// It used to land beside the original, which meant a cutout of something
    /// dragged in from a web page ended up in a temp directory the user had no
    /// reason to know about. Downloads is where the rest of the app puts things
    /// and where people already look.
    static func save(_ result: Result, basedOn original: URL,
                     to directory: URL = AppSettings.downloadsDirectory) throws -> URL {
        let stem = original.deletingPathExtension().lastPathComponent
        var destination = directory.appendingPathComponent("\(stem)-cutout.png")
        var counter = 2
        while FileManager.default.fileExists(atPath: destination.path) {
            destination = directory.appendingPathComponent("\(stem)-cutout \(counter).png")
            counter += 1
        }
        try result.pngData.write(to: destination)
        return destination
    }
}
