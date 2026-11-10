import { isFolder, type BookmarkFolder, type BookmarkLink, type BookmarkNode, type BrowserLibrary, favourites, withFavourites } from '../library/library'

export interface ImportedLink {
	kind: 'link'
	title: string
	url: string
}

export interface ImportedFolder {
	kind: 'folder'
	title: string
	children: ImportedNode[]
}

export type ImportedNode = ImportedLink | ImportedFolder

export interface BookmarkImport {
	// What the source showed on its bookmarks/favourites bar.
	bar: ImportedNode[]
	// Everything else, as top-level folders and links.
	other: ImportedNode[]
	// Entries that couldn't be imported, said out loud rather than dropped.
	skipped: string[]
}

export function importedLink(title: string, url: string): ImportedLink {
	return { kind: 'link', title, url }
}

export function importedFolder(title: string, children: ImportedNode[]): ImportedFolder {
	return { kind: 'folder', title, children }
}

// Only http(s) addresses become bookmarks, the same rule the start page
// shortcuts use. javascript: bookmarklets would run in whatever page is open.
export function acceptsBookmarkUrl(url: string): boolean {
	if (!URL.canParse(url)) return false
	const { protocol } = new URL(url)
	return protocol === 'https:' || protocol === 'http:'
}

export function countLinks(nodes: readonly ImportedNode[]): number {
	return nodes.reduce((sum, node) => sum + (node.kind === 'link' ? 1 : countLinks(node.children)), 0)
}

export interface MergeOutcome {
	library: BrowserLibrary
	addedToBar: number
	addedToFolder: number
	folderTitle: string
}

interface Merged {
	children: BookmarkNode[]
	added: number
}

type FolderMatch = (existing: BookmarkFolder, incoming: ImportedFolder) => boolean

// Ids are handed out parent first, so the order is the same on every port.
function toLibraryNodes(nodes: readonly ImportedNode[], source: string, now: number, newId: () => string): Merged {
	let added = 0
	const build = (node: ImportedNode): BookmarkNode => {
		if (node.kind === 'link') {
			added++
			return { id: newId(), title: node.title, url: node.url, updatedAt: now, importedFrom: source }
		}
		const id = newId()
		return { id, title: node.title, children: node.children.map(build), updatedAt: now, importedFrom: source }
	}
	return { children: nodes.map(build), added }
}

// Adds what isn't there yet and touches nothing else. A link is "there" when a
// direct child has its address; a folder when `matches` finds one, and then we
// go one level down. The user's own edits (renames, moves, extra links) stay.
function mergeChildren(existing: readonly BookmarkNode[], incoming: readonly ImportedNode[], matches: FolderMatch, source: string, now: number, newId: () => string): Merged {
	const urls = new Set(existing.filter((n) => !isFolder(n)).map((n) => (n as BookmarkLink).url))
	const children = [...existing]
	let added = 0
	for (const node of incoming) {
		if (node.kind === 'link') {
			if (urls.has(node.url)) continue
			const built = toLibraryNodes([node], source, now, newId)
			children.push(...built.children)
			added += built.added
			continue
		}
		const index = children.findIndex((n) => isFolder(n) && matches(n, node))
		const match = children[index]
		if (index === -1 || !match || !isFolder(match)) {
			const built = toLibraryNodes([node], source, now, newId)
			children.push(...built.children)
			added += built.added
			continue
		}
		const inner = mergeChildren(match.children, node.children, matches, source, now, newId)
		if (inner.added === 0) continue
		children[index] = { ...match, children: inner.children, updatedAt: now }
		added += inner.added
	}
	return { children, added }
}

// Bar items join the Favourites bar; everything else lands in one
// "Imported from <source>" folder, found again by its importedFrom mark even if
// renamed. Re-importing only adds: nothing is ever removed, renamed or reordered.
export function mergeImport(library: BrowserLibrary, result: BookmarkImport, source: string, now: number, newId: () => string): MergeOutcome {
	const folderTitle = `Imported from ${source}`
	const bar = mergeChildren(favourites(library), result.bar, (existing, incoming) => existing.title === incoming.title && existing.importedFrom === source, source, now, newId)
	let merged = withFavourites(library, bar.children)
	if (result.other.length === 0) return { library: merged, addedToBar: bar.added, addedToFolder: 0, folderTitle }
	const byTitle: FolderMatch = (existing, incoming) => existing.title === incoming.title
	const index = merged.bookmarks.findIndex((n) => isFolder(n) && n.importedFrom === source)
	const existing = merged.bookmarks[index]
	if (index === -1 || !existing || !isFolder(existing)) {
		const id = newId()
		const built = toLibraryNodes(result.other, source, now, newId)
		const imported: BookmarkFolder = { id, title: folderTitle, children: built.children, updatedAt: now, importedFrom: source }
		merged = { ...merged, bookmarks: [...merged.bookmarks, imported] }
		return { library: merged, addedToBar: bar.added, addedToFolder: built.added, folderTitle }
	}
	const inner = mergeChildren(existing.children, result.other, byTitle, source, now, newId)
	if (inner.added === 0) return { library: merged, addedToBar: bar.added, addedToFolder: 0, folderTitle }
	const bookmarks = [...merged.bookmarks]
	bookmarks[index] = { ...existing, children: inner.children, updatedAt: now }
	return { library: { ...merged, bookmarks }, addedToBar: bar.added, addedToFolder: inner.added, folderTitle }
}
