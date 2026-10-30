import SwiftUI

/// Bookmarks, the Favourites bar and the Reading List, shared by every window
/// and kept on disk at `~/Library/Application Support/Krackn/library.json`.
///
/// The one deliberate exception to "the browser writes nothing": each entry
/// is something the user chose to save. Browsing itself still adds nothing
/// here — no history, no visit counts, no "frequently visited".
@MainActor
final class LibraryStore: ObservableObject {
    static let shared = LibraryStore()

    @Published var library: BrowserLibrary {
        didSet { if library != oldValue { save() } }
    }

    static var fileURL: URL {
        // Lets the test and render harnesses point at a scratch file, so they
        // can never read or overwrite the user's real bookmarks.
        if let override = ProcessInfo.processInfo.environment["KRACKN_LIBRARY_FILE"] {
            return URL(fileURLWithPath: override)
        }
        return FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("Krackn", isDirectory: true)
            .appendingPathComponent("library.json")
    }

    private init() {
        if let data = try? Data(contentsOf: Self.fileURL),
           let saved = try? JSONDecoder().decode(BrowserLibrary.self, from: data) {
            library = saved
        } else {
            library = BrowserLibrary()
        }
    }

    private func save() {
        let url = Self.fileURL
        let snapshot = library
        Task.detached(priority: .utility) {
            try? FileManager.default.createDirectory(
                at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
            if let data = try? JSONEncoder().encode(snapshot) {
                try? data.write(to: url, options: .atomic)
            }
        }
    }

    // MARK: Favourites and bookmarks

    func addFavourite(title: String, url: String) {
        guard !library.favourites.contains(where: { $0.url == url }) else { return }
        library.favourites.append(.link(title, url))
    }

    func isFavourite(_ url: String) -> Bool {
        library.favourites.contains { $0.url == url }
    }

    func remove(_ ids: Set<UUID>) {
        library.bookmarks = LibraryOps.remove(ids, from: library.bookmarks)
    }

    func rename(_ id: UUID, to title: String) {
        library.bookmarks = LibraryOps.rename(id, to: title, in: library.bookmarks)
    }

    @discardableResult
    func newFolder(grouping ids: Set<UUID>) -> UUID? {
        if ids.isEmpty {
            let folder = BookmarkNode.folder("New Folder")
            library.bookmarks.append(folder)
            return folder.id
        }
        let result = LibraryOps.groupIntoFolder(ids, title: "New Folder", in: library.bookmarks)
        library.bookmarks = result.nodes
        return result.folderID
    }

    func moveFavourite(from: Int, to: Int) {
        library.favourites = LibraryOps.move(library.favourites, from: from, to: to)
    }

    // MARK: Reading List

    func addToReadingList(title: String, url: String) {
        guard !library.readingList.contains(where: { $0.url == url }) else { return }
        library.readingList.insert(ReadingItem(title: title, url: url), at: 0)
    }

    func removeReading(_ id: UUID) {
        library.readingList.removeAll { $0.id == id }
    }

    func setRead(_ id: UUID, _ read: Bool) {
        if let i = library.readingList.firstIndex(where: { $0.id == id }) {
            library.readingList[i].isRead = read
        }
    }
}

/// A browser download, for the downloads list.
enum DownloadEvent {
    case started(ObjectIdentifier, URL)
    case finished(ObjectIdentifier, String)
    case failed(ObjectIdentifier, String)
}

struct DownloadItem: Identifiable, Equatable {
    enum State: Equatable { case running, done(String), failed(String) }
    let id: ObjectIdentifier
    let file: URL
    var state: State
    let started = Date()
}
