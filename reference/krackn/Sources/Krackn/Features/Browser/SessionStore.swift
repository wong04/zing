import Foundation
import WebKit

/// One tab as it survives a quit.
struct TabRecord: Codable, Equatable {
    var url: String
    var title: String
    var isPinned: Bool
    var isActive: Bool
    /// WebKit's opaque snapshot of the tab — full back/forward list, form
    /// contents and scroll position. Restoring from this is the difference
    /// between "the page is open again" and "you're where you left off".
    /// Optional because it's only available once a tab has actually loaded.
    var interactionState: Data?

    /// A tab worth saving. The blank new-tab page isn't; restoring a window
    /// full of empty tabs is worse than restoring nothing.
    var isRestorable: Bool {
        guard let scheme = URL(string: url)?.scheme?.lowercased() else { return false }
        return scheme == "http" || scheme == "https"
    }
}

/// Saves and reloads the open tabs.
///
/// This is deliberately at odds with the rest of the browser's privacy model,
/// and the conflict is not resolvable by cleverness: remembering which pages
/// were open *means* writing a list of visited URLs to disk. There is no
/// version of "pick up where you left off" that leaves no trace. So it's a
/// setting, it's off unless asked for, the file is somewhere the user can find
/// and delete, and the privacy copy in Settings changes to tell the truth when
/// it's on.
enum SessionStore {

    /// Beyond this the file stops being a convenience and becomes a liability
    /// — both in size and in how much history it records.
    static let maximumTabs = 40
    /// WebKit's interaction state can run to megabytes on a heavy tab. Past
    /// this only the URL is kept, which still reopens the page.
    static let maximumStateBytes = 512 * 1024

    static var fileURL: URL {
        let base = FileManager.default
            .urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("Krackn", isDirectory: true)
        try? FileManager.default.createDirectory(at: base, withIntermediateDirectories: true)
        return base.appendingPathComponent("session.json")
    }

    // MARK: - Pure codec

    /// Trims a set of records down to what's worth persisting. Pure, so the
    /// rules — what's restorable, the caps, pinned tabs taking priority — can
    /// be tested without touching the disk or a web view.
    static func prepare(_ records: [TabRecord]) -> [TabRecord] {
        var kept = records.filter(\.isRestorable).map { record -> TabRecord in
            var copy = record
            if let state = copy.interactionState, state.count > maximumStateBytes {
                copy.interactionState = nil
            }
            return copy
        }
        // Pinned tabs are the ones the user explicitly said to keep, so they
        // survive the cap first.
        if kept.count > maximumTabs {
            let pinned = kept.filter(\.isPinned)
            let loose = kept.filter { !$0.isPinned }
            kept = Array((pinned + loose).prefix(maximumTabs))
        }
        return kept
    }

    static func encode(_ records: [TabRecord]) throws -> Data {
        try JSONEncoder().encode(prepare(records))
    }

    static func decode(_ data: Data) -> [TabRecord] {
        // A corrupt or half-written file must not stop the browser opening.
        ((try? JSONDecoder().decode([TabRecord].self, from: data)) ?? [])
            .filter(\.isRestorable)
    }

    // MARK: - Disk

    static func save(_ records: [TabRecord]) {
        guard let data = try? encode(records), !records.isEmpty else {
            clear()
            return
        }
        try? data.write(to: fileURL, options: .atomic)
    }

    static func load() -> [TabRecord] {
        guard let data = try? Data(contentsOf: fileURL) else { return [] }
        return decode(data)
    }

    static func clear() {
        try? FileManager.default.removeItem(at: fileURL)
    }

    static var hasSavedSession: Bool {
        FileManager.default.fileExists(atPath: fileURL.path)
    }
}
