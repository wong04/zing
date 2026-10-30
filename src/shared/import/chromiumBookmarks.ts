import { acceptsBookmarkUrl, importedFolder, importedLink, type BookmarkImport, type ImportedNode } from './importResult'

export class ChromiumBookmarksFormatError extends Error {
	override name = 'ChromiumBookmarksFormatError'
}

interface ChromiumNode {
	type: 'url' | 'folder'
	name: string
	url?: string
	children?: ChromiumNode[]
}

interface ChromiumRoots {
	bookmark_bar?: ChromiumNode
	other?: ChromiumNode
	synced?: ChromiumNode
}

// Chrome, Edge, Brave and Dia keep bookmarks in a JSON file named
// `Bookmarks` inside each profile folder.
export function parseChromiumBookmarks(json: string): BookmarkImport {
	const data: unknown = JSON.parse(json)
	const roots = (data as { roots?: ChromiumRoots })?.roots
	if (!roots || typeof roots !== 'object') throw new ChromiumBookmarksFormatError('No "roots" object; this is not a Chromium Bookmarks file')
	const skipped: string[] = []
	const bar = convertChildren(roots.bookmark_bar, skipped)
	const other: ImportedNode[] = [...convertChildren(roots.other, skipped)]
	const mobile = convertChildren(roots.synced, skipped)
	if (mobile.length > 0) other.push(importedFolder(roots.synced?.name || 'Mobile bookmarks', mobile))
	return { bar, other, skipped }
}

function convertChildren(node: ChromiumNode | undefined, skipped: string[]): ImportedNode[] {
	if (!node?.children) return []
	return node.children.flatMap((child) => convert(child, skipped))
}

function convert(node: ChromiumNode, skipped: string[]): ImportedNode[] {
	if (node.type === 'folder') return [importedFolder(node.name, convertChildren(node, skipped))]
	if (node.type !== 'url') throw new ChromiumBookmarksFormatError(`Unknown node type "${String(node.type)}"`)
	const url = node.url ?? ''
	if (!acceptsBookmarkUrl(url)) {
		skipped.push(`${node.name || url}: only http(s) addresses can be bookmarks`)
		return []
	}
	return [importedLink(node.name || url, url)]
}
