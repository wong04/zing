// The browser's saved things: bookmarks, the Favourites bar, the Reading List.
// Ported from Krackn's BrowserLibrary.swift.
//
// Privacy note: this is the one store the browser writes on purpose, because
// every item in it is something the user explicitly chose to keep. Nothing is
// added by browsing; the session itself still leaves nothing behind.

export interface BookmarkLink {
	id: string
	title: string
	url: string
	updatedAt: number
}

export interface BookmarkFolder {
	id: string
	title: string
	children: BookmarkNode[]
	updatedAt: number
}

export type BookmarkNode = BookmarkLink | BookmarkFolder

export interface ReadingItem {
	id: string
	title: string
	url: string
	added: number
	isRead: boolean
	updatedAt: number
}

export interface BrowserLibrary {
	// The first node is always the Favourites folder, whose children are what
	// the Favourites bar shows.
	bookmarks: BookmarkNode[]
	readingList: ReadingItem[]
}

// Fixed so the Favourites folder can always be found, renamed or not.
export const FAVOURITES_ID = 'fab00000-0000-4000-8000-000000000001'

export function isFolder(node: BookmarkNode): node is BookmarkFolder {
	return 'children' in node
}

export function link(title: string, url: string, now = Date.now()): BookmarkLink {
	return { id: crypto.randomUUID(), title, url, updatedAt: now }
}

export function folder(title: string, children: BookmarkNode[] = [], id: string = crypto.randomUUID(), now = Date.now()): BookmarkFolder {
	return { id, title, children, updatedAt: now }
}

export function readingItem(title: string, url: string, now = Date.now()): ReadingItem {
	return { id: crypto.randomUUID(), title, url, added: now, isRead: false, updatedAt: now }
}

export function emptyLibrary(): BrowserLibrary {
	return { bookmarks: [folder('Favourites', [], FAVOURITES_ID)], readingList: [] }
}

export function favourites(library: BrowserLibrary): BookmarkNode[] {
	const fav = library.bookmarks.find((node) => node.id === FAVOURITES_ID)
	if (!fav || !isFolder(fav)) return []
	return fav.children
}

export function withFavourites(library: BrowserLibrary, children: BookmarkNode[]): BrowserLibrary {
	const index = library.bookmarks.findIndex((node) => node.id === FAVOURITES_ID)
	if (index === -1) {
		return { ...library, bookmarks: [folder('Favourites', children, FAVOURITES_ID), ...library.bookmarks] }
	}
	const bookmarks = [...library.bookmarks]
	bookmarks[index] = { ...(bookmarks[index] as BookmarkFolder), children }
	return { ...library, bookmarks }
}

// Removes every node whose id is in `ids`, at any depth. The Favourites folder
// itself can't be removed: the bar would have nowhere to live.
export function remove(ids: ReadonlySet<string>, nodes: BookmarkNode[]): BookmarkNode[] {
	const out: BookmarkNode[] = []
	for (const node of nodes) {
		if (ids.has(node.id) && node.id !== FAVOURITES_ID) continue
		out.push(isFolder(node) ? { ...node, children: remove(ids, node.children) } : node)
	}
	return out
}

export function rename(id: string, title: string, nodes: BookmarkNode[], now = Date.now()): BookmarkNode[] {
	const trimmed = title.trim()
	if (!trimmed) return nodes
	return nodes.map((node) => {
		const copy = node.id === id ? { ...node, title: trimmed, updatedAt: now } : node
		return isFolder(copy) ? { ...copy, children: rename(id, trimmed, copy.children, now) } : copy
	})
}

export function find(id: string, nodes: BookmarkNode[]): BookmarkNode | undefined {
	for (const node of nodes) {
		if (node.id === id) return node
		if (!isFolder(node)) continue
		const hit = find(id, node.children)
		if (hit) return hit
	}
	return undefined
}

// Depth-first, in order.
export function collect(ids: ReadonlySet<string>, nodes: BookmarkNode[]): BookmarkNode[] {
	const out: BookmarkNode[] = []
	for (const node of nodes) {
		if (ids.has(node.id)) {
			out.push(node)
			continue
		}
		if (isFolder(node)) out.push(...collect(ids, node.children))
	}
	return out
}

// Wraps the selected items in a new folder, placed where the first of them
// was (Safari's Option-click on New Folder). Selected items in different
// folders are gathered together; the folder lands in the parent of the first.
export function groupIntoFolder(ids: ReadonlySet<string>, title: string, nodes: BookmarkNode[]): { nodes: BookmarkNode[]; folderId: string | undefined } {
	const selected = collect(ids, nodes).filter((node) => node.id !== FAVOURITES_ID)
	if (selected.length === 0) return { nodes, folderId: undefined }
	const wrapper = folder(title, selected)
	let placed = false
	const rebuild = (list: BookmarkNode[]): BookmarkNode[] => {
		const out: BookmarkNode[] = []
		for (const node of list) {
			if (ids.has(node.id) && node.id !== FAVOURITES_ID) {
				if (!placed) {
					out.push(wrapper)
					placed = true
				}
				continue
			}
			out.push(isFolder(node) ? { ...node, children: rebuild(node.children) } : node)
		}
		return out
	}
	return { nodes: rebuild(nodes), folderId: wrapper.id }
}

// Every link in a folder, depth-first: what Ctrl-clicking a Favourites bar
// folder opens.
export function urls(node: BookmarkNode): string[] {
	if (!isFolder(node)) return [node.url]
	return node.children.flatMap(urls)
}

export interface VisibleRow {
	node: BookmarkNode
	depth: number
}

// The rows a tree shows with some folders open, in display order: what the
// arrow keys move through.
export function visibleRows(nodes: BookmarkNode[], expanded: ReadonlySet<string>, depth = 0): VisibleRow[] {
	const rows: VisibleRow[] = []
	for (const node of nodes) {
		rows.push({ node, depth })
		if (isFolder(node) && expanded.has(node.id)) rows.push(...visibleRows(node.children, expanded, depth + 1))
	}
	return rows
}

// `to` is an insertion point in the *original* list ("before the item now at
// `to`"), the same convention as SwiftUI's onMove.
export function move<T>(list: readonly T[], from: number, to: number): T[] {
	const inRange = from >= 0 && from < list.length && to >= 0 && to <= list.length
	if (!inRange || to === from || to === from + 1) return [...list]
	const copy = [...list]
	const [item] = copy.splice(from, 1)
	copy.splice(to > from ? to - 1 : to, 0, item as T)
	return copy
}
