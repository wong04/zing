import Foundation

// Bookmarks, the Reading List, and the tab-shortcut arithmetic.

var pass = 0, fail = 0
func out(_ t: String) { print(t) }
func check(_ name: String, _ condition: Bool, _ detail: @autoclosure () -> String = "") {
    if condition { pass += 1; out("  ok   \(name)") }
    else { fail += 1; out("  FAIL \(name) \(detail())") }
}

out("── tab shortcuts")
check("⌃Tab wraps last → first", TabNavigation.next(after: 4, count: 5) == 0)
check("⌃Tab moves forward", TabNavigation.next(after: 1, count: 5) == 2)
check("⌃⇧Tab wraps first → last", TabNavigation.previous(before: 0, count: 5) == 4)
check("⌘1 is the first tab", TabNavigation.index(forShortcut: 1, count: 5) == 0)
check("⌘5 is the fifth", TabNavigation.index(forShortcut: 5, count: 5) == 4)
check("⌘7 with five tabs does nothing", TabNavigation.index(forShortcut: 7, count: 5) == nil)
check("⌘9 is always the last", TabNavigation.index(forShortcut: 9, count: 12) == 11)
check("⌘9 with one tab is that tab", TabNavigation.index(forShortcut: 9, count: 1) == 0)
check("no tabs, no target", TabNavigation.next(after: 0, count: 0) == nil)

out("── reopen closed tabs: newest first, bounded")
var closed = ClosedStack<String>(capacity: 3)
for t in ["a", "b", "c", "d"] { closed.push(t) }
check("capped at capacity", closed.items == ["b", "c", "d"], "\(closed.items)")
check("pops newest first", closed.pop() == "d" && closed.pop() == "c")

out("── bookmarks")
var lib = BrowserLibrary()
check("a fresh library has a Favourites folder", lib.bookmarks.first?.id == BrowserLibrary.favouritesID)
lib.favourites = [.link("Apple", "https://apple.com"), .link("Wiki", "https://wikipedia.org")]
check("favourites live inside that folder", lib.bookmarks.first?.children?.count == 2)
let docs = BookmarkNode.folder("Docs", [.link("MDN", "https://developer.mozilla.org"),
                                        .folder("Inner", [.link("Swift", "https://swift.org")])])
lib.bookmarks.append(docs)

let removed = LibraryOps.remove([BrowserLibrary.favouritesID], from: lib.bookmarks)
check("the Favourites folder can't be deleted", removed.first?.id == BrowserLibrary.favouritesID)

let mdn = docs.children![0]
let noMDN = LibraryOps.remove([mdn.id], from: lib.bookmarks)
check("removes at any depth", LibraryOps.find(mdn.id, in: noMDN) == nil)
check("leaves siblings alone", LibraryOps.find(docs.children![1].id, in: noMDN) != nil)

let renamed = LibraryOps.rename(mdn.id, to: "  Mozilla  ", in: lib.bookmarks)
check("rename trims", LibraryOps.find(mdn.id, in: renamed)?.title == "Mozilla")
check("blank rename is ignored", LibraryOps.find(mdn.id, in: LibraryOps.rename(mdn.id, to: "  ", in: lib.bookmarks))?.title == "MDN")

check("⌘-click a folder opens every link inside, nested too",
      LibraryOps.urls(in: docs) == ["https://developer.mozilla.org", "https://swift.org"])

let fav = lib.favourites
let grouped = LibraryOps.groupIntoFolder([fav[0].id, fav[1].id], title: "New Folder", in: lib.bookmarks)
let newFolder = grouped.folderID.flatMap { LibraryOps.find($0, in: grouped.nodes) }
check("Option-New Folder wraps the selection", newFolder?.children?.count == 2)
check("the folder lands where the first item was",
      grouped.nodes.first?.children?.first?.id == grouped.folderID)
check("the items aren't duplicated", LibraryOps.collect([fav[0].id], in: grouped.nodes).count == 1)

out("── keyboard navigation follows what's visible")
let collapsed = LibraryOps.visibleRows(lib.bookmarks, expanded: [])
check("collapsed: only top-level rows", collapsed.count == lib.bookmarks.count)
let open = LibraryOps.visibleRows(lib.bookmarks, expanded: [BrowserLibrary.favouritesID, docs.id])
check("open folders add their children in order", open.map(\.node.title) ==
      ["Favourites", "Apple", "Wiki", "Docs", "MDN", "Inner"], "\(open.map(\.node.title))")
check("children are indented one level", open[1].depth == 1 && open[0].depth == 0)

out("── reordering")
check("move right", LibraryOps.move(["a", "b", "c"], from: 0, to: 2) == ["b", "a", "c"])
check("move to the end", LibraryOps.move(["a", "b", "c"], from: 0, to: 3) == ["b", "c", "a"])
check("out of range is a no-op", LibraryOps.move(["a", "b"], from: 5, to: 0) == ["a", "b"])

out("── the library round-trips through JSON")
lib.readingList = [ReadingItem(title: "Article", url: "https://example.com/a")]
let data = try! JSONEncoder().encode(lib)
let back = try! JSONDecoder().decode(BrowserLibrary.self, from: data)
check("encode → decode is lossless", back == lib)

out(""); out("\(pass) passed, \(fail) failed")
exit(fail == 0 ? 0 : 1)
