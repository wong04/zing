import SwiftUI
import AppKit

// Renders Resources/AppIcon.iconset from the logo's own vector paths:
// the kraken in polished silver, lit from the top left, on a black
// Apple-grid app-icon body.
//
//   ./Tools/make-icon.sh
//
// Each size is drawn from vectors at its exact pixel size rather than scaled
// down from 1024, so the 16 and 32 px icons stay crisp.

// KracknLogo.swift's view defaults reference Palette; the paths and shape this
// tool uses don't, so a stub is enough (as in Tests/logo).
enum Palette {
    static var accent: Color { .white }
    static var onAccent: Color { .black }
}

/// One icon at `px` pixels, laid out on Apple's 1024 grid: an 824pt body
/// inset 100 from each edge, continuous corners of radius 185.4, and the
/// standard soft drop shadow beneath it.
struct AppIconView: View {
    let px: CGFloat
    var body: some View {
        let k = px / 1024
        let shape = RoundedRectangle(cornerRadius: 185.4 * k, style: .continuous)
        ZStack {
            shape
                .fill(LinearGradient(colors: [Color(white: 0.11), .black],
                                     startPoint: .top, endPoint: .bottom))
                .overlay(shape.strokeBorder(Color.white.opacity(0.08), lineWidth: max(1, 2 * k)))
                .frame(width: 824 * k, height: 824 * k)
                .shadow(color: .black.opacity(0.28), radius: 10 * k, y: 10 * k)
            ZStack {
                SilverKraken(k: k)
                SVGShape(paths: KracknLogoArt.eyes).fill(Color.black)
            }
            .frame(width: 540 * k,
                   height: 540 * k * KracknLogoArt.viewBox.height / KracknLogoArt.viewBox.width)
            // A touch above centre: the tentacles visually weigh the art down.
            .offset(y: -8 * k)
        }
        .frame(width: px, height: px)
    }
}

/// The kraken as polished metal under a light from the top left.
///
/// Three layers, each clipped to the silhouette: a diagonal run of light and
/// dark bands (what a curved chrome surface reflects), a soft hotspot where
/// the light falls, and a rim — bright on the lit edges, dim on the far ones —
/// that gives the flat shape a bevel.
struct SilverKraken: View {
    let k: CGFloat
    private var shape: SVGShape { SVGShape(paths: KracknLogoArt.body) }

    var body: some View {
        shape
            .fill(LinearGradient(stops: [
                .init(color: Color(white: 1.00), location: 0.00),
                .init(color: Color(white: 0.86), location: 0.18),
                .init(color: Color(white: 0.46), location: 0.37),
                .init(color: Color(white: 0.66), location: 0.47),
                .init(color: Color(white: 1.00), location: 0.555),
                .init(color: Color(white: 0.84), location: 0.62),
                .init(color: Color(white: 0.50), location: 0.82),
                .init(color: Color(white: 0.32), location: 1.00),
            ], startPoint: .topLeading, endPoint: .bottomTrailing))
            // A faint cool cast keeps it reading as steel, not grey paint.
            .overlay(shape.fill(Color(red: 0.55, green: 0.66, blue: 0.85).opacity(0.10)))
            // Where the light falls.
            .overlay(shape.fill(RadialGradient(
                colors: [.white.opacity(0.75), .white.opacity(0)],
                center: UnitPoint(x: 0.30, y: 0.18), startRadius: 0, endRadius: 260 * k)))
            // The bevel: half the stroke falls outside and is masked away.
            .overlay(shape
                .stroke(LinearGradient(colors: [.white, Color(white: 0.55), Color(white: 0.18)],
                                       startPoint: .topLeading, endPoint: .bottomTrailing),
                        lineWidth: max(1, 7 * k))
                .mask(shape)
                .opacity(px16(k) ? 0 : 0.9))
    }

    /// At 16 px the rim is a pixel wide and only muddies the outline.
    private func px16(_ k: CGFloat) -> Bool { k * 1024 < 24 }
}

let out = URL(fileURLWithPath: CommandLine.arguments[1], isDirectory: true)
try? FileManager.default.createDirectory(at: out, withIntermediateDirectories: true)
let sizes: [(String, CGFloat)] = [
    ("icon_16x16", 16), ("icon_16x16@2x", 32), ("icon_32x32", 32), ("icon_32x32@2x", 64),
    ("icon_128x128", 128), ("icon_128x128@2x", 256), ("icon_256x256", 256),
    ("icon_256x256@2x", 512), ("icon_512x512", 512), ("icon_512x512@2x", 1024),
]
MainActor.assumeIsolated {
    for (name, px) in sizes {
        let renderer = ImageRenderer(content: AppIconView(px: px))
        renderer.scale = 1
        guard let cg = renderer.cgImage else { print("failed \(name)"); continue }
        let rep = NSBitmapImageRep(cgImage: cg)
        try? rep.representation(using: .png, properties: [:])?
            .write(to: out.appendingPathComponent("\(name).png"))
    }
    print("rendered \(sizes.count) sizes into \(out.path)")
}
