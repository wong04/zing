import AppKit
import SwiftUI

/// Site icons for the tab strip.
///
/// Keyed by the icon's own address when the page declares one, and by the
/// *full host* otherwise — never by registrable domain. Keying by domain made
/// every Google tab wear whichever Google icon loaded first (Gmail's on Docs,
/// Slides and Drive); Docs, Sheets and Slides even share a host, so only the
/// page's declared icon can tell them apart. See `FaviconChoice`.
///
/// Memory only. A favicon cache on disk is a dated list of every site visited
/// — a browsing history in all but name — and this browser keeps none.
@MainActor
final class FaviconStore: ObservableObject {
    static let shared = FaviconStore()

    /// A `nil` value means "looked, there isn't one", so a miss isn't
    /// re-fetched on every redraw.
    @Published private(set) var icons: [String: NSImage?] = [:]
    private var inFlight: Set<String> = []

    private init() {}

    private static func iconKey(_ url: String) -> String { "icon:" + url }
    private static func hostKey(_ host: String) -> String { "host:" + host.lowercased() }

    /// The page's own icon if it declared one and it's loaded; otherwise the
    /// host's conventional icon.
    func icon(iconURL: String?, host: String) -> NSImage? {
        if let iconURL, case .some(.some(let image)) = icons[Self.iconKey(iconURL)] { return image }
        if case .some(.some(let image)) = icons[Self.hostKey(host)] { return image }
        return nil
    }

    /// Fetches one declared icon, once per session.
    func load(iconURL: String) {
        fetchOnce(key: Self.iconKey(iconURL), candidates: [iconURL])
    }

    /// The conventional locations, for pages that declare nothing.
    func load(host: String) {
        guard !host.isEmpty else { return }
        fetchOnce(key: Self.hostKey(host), candidates: [
            "https://\(host)/favicon.ico",
            "https://\(host)/apple-touch-icon.png",
            "https://\(host)/favicon.png",
        ])
    }

    private func fetchOnce(key: String, candidates: [String]) {
        guard icons[key] == nil, !inFlight.contains(key) else { return }
        inFlight.insert(key)
        Task { [weak self] in
            // `Data` crosses the actor boundary, not `NSImage`, which isn't
            // Sendable; decoding happens on the main actor where it's drawn.
            let bytes = await Self.fetch(candidates)
            await MainActor.run {
                self?.icons[key] = .some(bytes.flatMap { Self.scaled(from: $0) })
                self?.inFlight.remove(key)
            }
        }
    }

    /// Tab chips are tiny; a 512px apple-touch-icon held at full size across
    /// twenty tabs is a lot of memory for nothing.
    private static func scaled(from data: Data) -> NSImage? {
        guard let image = NSImage(data: data), image.size.width > 0 else { return nil }
        let target = NSSize(width: 32, height: 32)
        let scaled = NSImage(size: target)
        scaled.lockFocus()
        NSGraphicsContext.current?.imageInterpolation = .high
        image.draw(in: NSRect(origin: .zero, size: target),
                   from: .zero, operation: .copy, fraction: 1)
        scaled.unlockFocus()
        return scaled
    }

    private nonisolated static func fetch(_ candidates: [String]) async -> Data? {
        for candidate in candidates {
            guard let url = URL(string: candidate),
                  let scheme = url.scheme?.lowercased(),
                  scheme == "https" || scheme == "http" || scheme == "data"
            else { continue }
            var request = URLRequest(url: url)
            request.timeoutInterval = 6
            if let agent = BrowserTab.userAgent {
                request.setValue(agent, forHTTPHeaderField: "User-Agent")
            }
            guard let (data, response) = try? await URLSession.shared.data(for: request),
                  scheme == "data" || ((response as? HTTPURLResponse).map {
                      (200..<300).contains($0.statusCode) } ?? false),
                  // A "not found" page served as HTML is not an icon.
                  data.count > 60, data.count < 2_000_000
            else { continue }
            return data
        }
        return nil
    }
}

/// A tab's site icon, falling back to a glyph while it loads or when the site
/// has none.
struct Favicon: View {
    let host: String
    /// The icon the page itself declared, when known — see `BrowserTab.iconURL`.
    var iconURL: String? = nil
    var size: CGFloat = 13
    var tint: Color = Palette.textSecondary
    @ObservedObject private var store = FaviconStore.shared

    var body: some View {
        Group {
            if let image = store.icon(iconURL: iconURL, host: host) {
                Image(nsImage: image)
                    .interpolation(.high)
                    .resizable()
                    .aspectRatio(contentMode: .fit)
            } else {
                Image(systemName: host.isEmpty ? "doc" : "globe")
                    .font(.system(size: size * 0.85, weight: .medium))
                    .foregroundStyle(tint)
            }
        }
        .frame(width: size, height: size)
        // Anything that shows an icon by host alone (favourites, bookmarks)
        // asks for the host's conventional one.
        .onAppear { if iconURL == nil, !host.isEmpty { store.load(host: host) } }
    }
}
