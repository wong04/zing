import Foundation

// The browser's saved things — bookmarks, the Favourites bar, the Reading
// List — and the small bits of arithmetic behind tab shortcuts. All pure, so
// `Tests/library` exercises them without a window.
//
// Privacy note: this is the one store the browser writes on purpose, because
// every item in it is something the user explicitly chose to keep. Nothing is
// added by browsing; the session itself still leaves nothing behind.

struct BookmarkNode: Codable, Identifiable, Equatable, Hashable {
    var id = UUID()
    var title: String
    /// Nil for a folder.
    var url: String?
    /// Non-nil for a folder, even an empty one.
    var children: [BookmarkNode]?

    var isFolder: Bool { children != nil }

    static func folder(_ title: String, id: UUID = UUID(), _ children: [BookmarkNode] = []) -> BookmarkNode {
        BookmarkNode(id: id, title: title, url: nil, children: children)
    }

    static func link(_ title: String, _ url: String) -> BookmarkNode {
        BookmarkNode(title: title, url: url, children: nil)
    }
}

struct ReadingItem: Codable, Identifiable, Equatable, Hashable {
    var id = UUID()
    var title: String
    var url: String
    var added = Date()
    var isRead = false
}

struct BrowserLibrary: Codable, Equatable {
    /// Fixed so the Favourites folder can always be found, renamed or not.
    static let favouritesID = UUID(uuidString: "FAB00000-0000-4000-8000-000000000001")!

    /// The bookmark tree. The first node is always the Favourites folder,
    /// whose children are what the Favourites bar shows.
    var bookmarks: [BookmarkNode] = [.folder("Favourites", id: BrowserLibrary.favouritesID)]
    var readingList: [ReadingItem] = []

    var favourites: [BookmarkNode] {
        get { bookmarks.first { $0.id == Self.favouritesID }?.children ?? [] }
        set {
            if let i = bookmarks.firstIndex(where: { $0.id == Self.favouritesID }) {
                bookmarks[i].children = newValue
            } else {
                bookmarks.insert(.folder("Favourites", id: Self.favouritesID, newValue), at: 0)
            }
        }
    }
}

enum LibraryOps {
    /// Removes every node whose id is in `ids`, at any depth. The Favourites
    /// folder itself can't be removed — the bar would have nowhere to live.
    static func remove(_ ids: Set<UUID>, from nodes: [BookmarkNode]) -> [BookmarkNode] {
        nodes.compactMap { node in
            if ids.contains(node.id), node.id != BrowserLibrary.favouritesID { return nil }
            var copy = node
            if let children = node.children { copy.children = remove(ids, from: children) }
            return copy
        }
    }

    static func rename(_ id: UUID, to title: String, in nodes: [BookmarkNode]) -> [BookmarkNode] {
        let trimmed = title.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return nodes }
        return nodes.map { node in
            var copy = node
            if node.id == id { copy.title = trimmed }
            if let children = node.children { copy.children = rename(id, to: trimmed, in: children) }
            return copy
        }
    }

    static func find(_ id: UUID, in nodes: [BookmarkNode]) -> BookmarkNode? {
        for node in nodes {
            if node.id == id { return node }
            if let children = node.children, let hit = find(id, in: children) { return hit }
        }
        return nil
    }

    /// Wraps the selected items in a new folder, placed where the first of
    /// them was — Safari's Option-click on New Folder. Selected items in
    /// different folders are gathered together; the folder lands in the
    /// parent of the first one.
    static func groupIntoFolder(_ ids: Set<UUID>, title: String,
                                in nodes: [BookmarkNode]) -> (nodes: [BookmarkNode], folderID: UUID?) {
        let selected = collect(ids, in: nodes).filter { $0.id != BrowserLibrary.favouritesID }
        guard !selected.isEmpty else { return (nodes, nil) }
        let folder = BookmarkNode.folder(title, selected)
        var placed = false
        func rebuild(_ list: [BookmarkNode]) -> [BookmarkNode] {
            var out: [BookmarkNode] = []
            for node in list {
                if ids.contains(node.id), node.id != BrowserLibrary.favouritesID {
                    if !placed { out.append(folder); placed = true }
                    continue
                }
                var copy = node
                if let children = node.children { copy.children = rebuild(children) }
                out.append(copy)
            }
            return out
        }
        return (rebuild(nodes), folder.id)
    }

    /// Depth-first, in order.
    static func collect(_ ids: Set<UUID>, in nodes: [BookmarkNode]) -> [BookmarkNode] {
        var out: [BookmarkNode] = []
        for node in nodes {
            if ids.contains(node.id) { out.append(node); continue }
            if let children = node.children { out += collect(ids, in: children) }
        }
        return out
    }

    /// Every link in a folder, depth-first — what ⌘-clicking a Favourites
    /// bar folder opens.
    static func urls(in node: BookmarkNode) -> [String] {
        if let url = node.url { return [url] }
        return (node.children ?? []).flatMap(urls(in:))
    }

    /// The rows a tree shows with some folders open, in display order — what
    /// ↑ and ↓ move through.
    static func visibleRows(_ nodes: [BookmarkNode], expanded: Set<UUID>,
                            depth: Int = 0) -> [(node: BookmarkNode, depth: Int)] {
        var rows: [(BookmarkNode, Int)] = []
        for node in nodes {
            rows.append((node, depth))
            if let children = node.children, expanded.contains(node.id) {
                rows += visibleRows(children, expanded: expanded, depth: depth + 1)
            }
        }
        return rows
    }

    /// `to` is an insertion point in the *original* list — "before the item
    /// now at `to`" — the same convention as SwiftUI's `onMove`.
    static func move<T>(_ list: [T], from: Int, to: Int) -> [T] {
        guard list.indices.contains(from), (0...list.count).contains(to),
              to != from, to != from + 1 else { return list }
        var copy = list
        let item = copy.remove(at: from)
        copy.insert(item, at: to > from ? to - 1 : to)
        return copy
    }
}

/// The arithmetic behind the tab shortcuts.
enum TabNavigation {
    /// ⌃Tab: the next tab, wrapping from the last to the first.
    static func next(after index: Int, count: Int) -> Int? {
        guard count > 0 else { return nil }
        return (index + 1) % count
    }

    /// ⌃⇧Tab: the previous tab, wrapping from the first to the last.
    static func previous(before index: Int, count: Int) -> Int? {
        guard count > 0 else { return nil }
        return (index - 1 + count) % count
    }

    /// ⌘1–⌘8 pick that tab if it exists; ⌘9 always means the last one.
    static func index(forShortcut number: Int, count: Int) -> Int? {
        guard count > 0, (1...9).contains(number) else { return nil }
        if number == 9 { return count - 1 }
        return number <= count ? number - 1 : nil
    }
}

/// Recently closed tabs, newest on top, for ⇧⌘T. Memory only: a list of
/// closed pages on disk would be exactly the history this browser doesn't keep.
struct ClosedStack<Element> {
    private(set) var items: [Element] = []
    var capacity = 25

    mutating func push(_ item: Element) {
        items.append(item)
        if items.count > capacity { items.removeFirst(items.count - capacity) }
    }

    mutating func pop() -> Element? { items.popLast() }
    var isEmpty: Bool { items.isEmpty }
}
