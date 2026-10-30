import { folder, isFolder, link, type BookmarkNode, type BrowserLibrary, FAVOURITES_ID, favourites, withFavourites } from '../library/library'

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

function toLibraryNodes(nodes: readonly ImportedNode[], now: number): BookmarkNode[] {
	return nodes.map((node) => (node.kind === 'link' ? link(node.title, node.url, now) : folder(node.title, toLibraryNodes(node.children, now), undefined, now)))
}

function libraryUrls(nodes: readonly BookmarkNode[]): Set<string> {
	const out = new Set<string>()
	for (const node of nodes) {
		if (isFolder(node)) libraryUrls(node.children).forEach((u) => out.add(u))
		else out.add(node.url)
	}
	return out
}

// Bar items join the Favourites bar (skipping addresses already on it);
// everything else lands in one "Imported from <source>" folder so a second
// import never scatters duplicates through the user's own tree.
export function mergeImport(library: BrowserLibrary, result: BookmarkImport, source: string, now = Date.now()): BrowserLibrary {
	const existingBar = favourites(library)
	const onBar = libraryUrls(existingBar)
	const newBar = toLibraryNodes(result.bar, now).filter((node) => isFolder(node) || !onBar.has(node.url))
	let merged = withFavourites(library, [...existingBar, ...newBar])
	if (result.other.length === 0) return merged
	const imported = folder(`Imported from ${source}`, toLibraryNodes(result.other, now), undefined, now)
	merged = { ...merged, bookmarks: [...merged.bookmarks.filter((n) => n.id === FAVOURITES_ID || n.title !== imported.title), imported] }
	return merged
}
