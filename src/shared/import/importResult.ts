import { FAVOURITES_ID, isFolder, type BookmarkFolder, type BookmarkLink, type BookmarkNode, type BrowserLibrary, favourites, withFavourites } from '../library/library'

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
	// The same array as the existing children when nothing changed.
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

// Every URL this source already put in the library, wherever the user has
// since moved it, so a re-import never adds it twice.
function importedUrls(nodes: readonly BookmarkNode[], source: string, into = new Set<string>()): Set<string> {
	for (const node of nodes) {
		if (isFolder(node)) importedUrls(node.children, source, into)
		else if (node.importedFrom === source) into.add(node.url)
	}
	return into
}

// Drops links already imported. A folder whose links were all imported goes
// too; one that never had links stays.
function withoutImported(nodes: readonly ImportedNode[], imported: ReadonlySet<string>): ImportedNode[] {
	return nodes.flatMap((node): ImportedNode[] => {
		if (node.kind === 'link') return imported.has(node.url) ? [] : [node]
		const children = withoutImported(node.children, imported)
		return children.length === 0 && countLinks(node.children) > 0 ? [] : [importedFolder(node.title, children)]
	})
}

// The first folder marked with the source whose parent isn't, depth first,
// outside Favourites: the import folder, wherever the user put it.
function findImportFolder(nodes: readonly BookmarkNode[], source: string, parentMarked: boolean): BookmarkFolder | undefined {
	for (const node of nodes) {
		if (!isFolder(node) || node.id === FAVOURITES_ID) continue
		const marked = node.importedFrom === source
		if (marked && !parentMarked) return node
		const found = findImportFolder(node.children, source, marked)
		if (found) return found
	}
	return undefined
}

// Copies only the folders on the way down to the replaced one.
function replaceFolder(nodes: BookmarkNode[], replacement: BookmarkFolder): BookmarkNode[] {
	let result = nodes
	nodes.forEach((node, index) => {
		if (!isFolder(node)) return
		const next = node.id === replacement.id ? replacement : { ...node, children: replaceFolder(node.children, replacement) }
		if (next !== replacement && next.children === node.children) return
		if (result === nodes) result = [...nodes]
		result[index] = next
	})
	return result
}

// Adds what isn't there yet and touches nothing else. A link is "there" when a
// direct child has its address or the source imported it before; a folder when
// `matches` finds one, and then we go one level down. The user's own edits
// (renames, moves, extra links) stay.
function mergeChildren(existing: BookmarkNode[], incoming: readonly ImportedNode[], matches: FolderMatch, imported: ReadonlySet<string>, source: string, now: number, newId: () => string): Merged {
	const urls = new Set(existing.filter((n) => !isFolder(n)).map((n) => (n as BookmarkLink).url))
	const children = [...existing]
	// Folders match only nodes from before this merge, each at most once, so two
	// same-titled incoming folders never merge into each other.
	const taken = new Set<number>()
	let changed = false
	let added = 0
	for (const node of incoming) {
		if (node.kind === 'link') {
			if (urls.has(node.url) || imported.has(node.url)) continue
			const built = toLibraryNodes([node], source, now, newId)
			children.push(...built.children)
			added += built.added
			changed = true
			continue
		}
		const index = existing.findIndex((n, i) => !taken.has(i) && isFolder(n) && matches(n, node))
		const match = children[index]
		if (index === -1 || !match || !isFolder(match)) {
			const built = toLibraryNodes(withoutImported([node], imported), source, now, newId)
			if (built.children.length === 0) continue
			children.push(...built.children)
			added += built.added
			changed = true
			continue
		}
		taken.add(index)
		const inner = mergeChildren(match.children, node.children, matches, imported, source, now, newId)
		if (inner.children === match.children) continue
		children[index] = { ...match, children: inner.children, updatedAt: now }
		added += inner.added
		changed = true
	}
	return { children: changed ? children : existing, added }
}

// Bar items join the Favourites bar; everything else lands in one
// "Imported from <source>" folder, found again by its importedFrom mark even if
// renamed or moved. Re-importing only adds: nothing is ever removed, renamed or
// reordered, and nothing the source imported before is added again.
export function mergeImport(library: BrowserLibrary, result: BookmarkImport, source: string, now: number, newId: () => string): MergeOutcome {
	const folderTitle = `Imported from ${source}`
	const imported = importedUrls(library.bookmarks, source)
	const bar = favourites(library)
	const barMerge = mergeChildren(bar, result.bar, (existing, incoming) => existing.title === incoming.title && existing.importedFrom === source, imported, source, now, newId)
	const merged = barMerge.children === bar ? library : withFavourites(library, barMerge.children)
	const unchanged = { library: merged, addedToBar: barMerge.added, addedToFolder: 0, folderTitle }
	if (result.other.length === 0) return unchanged
	const existing = findImportFolder(merged.bookmarks, source, false)
	if (!existing) {
		const other = withoutImported(result.other, imported)
		if (other.length === 0) return unchanged
		const id = newId()
		const built = toLibraryNodes(other, source, now, newId)
		const folder: BookmarkFolder = { id, title: folderTitle, children: built.children, updatedAt: now, importedFrom: source }
		return { ...unchanged, library: { ...merged, bookmarks: [...merged.bookmarks, folder] }, addedToFolder: built.added }
	}
	const byTitle: FolderMatch = (existing, incoming) => existing.title === incoming.title
	const inner = mergeChildren(existing.children, result.other, byTitle, imported, source, now, newId)
	if (inner.children === existing.children) return unchanged
	const bookmarks = replaceFolder(merged.bookmarks, { ...existing, children: inner.children, updatedAt: now })
	return { ...unchanged, library: { ...merged, bookmarks }, addedToFolder: inner.added }
}
