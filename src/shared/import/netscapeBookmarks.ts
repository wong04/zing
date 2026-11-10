import { acceptsBookmarkUrl, importedFolder, importedLink, type BookmarkImport, type ImportedFolder, type ImportedNode } from './importResult'

export class NetscapeBookmarksFormatError extends Error {
	override name = 'NetscapeBookmarksFormatError'
}

// The NETSCAPE-Bookmark-file-1 format every browser exports (Chrome, Edge,
// Firefox, Safari, Dia, Arc). It isn't well-formed HTML (<DT> and <p> are
// never closed), so it's tokenised rather than handed to a DOM parser, which
// also keeps this usable in the main process. A tag's attributes never hold a
// raw "<", so an unclosed tag fails at the next one instead of scanning the
// rest of the file.
const TOKEN = /<(\/?)(dl|h3|a)\b([^<>]*)>|<\/(h3|a)>/gi

interface Frame {
	folder: ImportedFolder
	isBar: boolean
}

export function parseNetscapeBookmarks(html: string): BookmarkImport {
	if (!/<!DOCTYPE\s+NETSCAPE-Bookmark-file-1>/i.test(html) && !/<DL>/i.test(html)) {
		throw new NetscapeBookmarksFormatError('Not a bookmarks HTML export (no NETSCAPE-Bookmark-file-1 doctype or <DL>)')
	}
	const root: ImportedFolder = importedFolder('', [])
	const stack: Frame[] = []
	const skipped: string[] = []
	let pendingFolder: { title: string; isBar: boolean } | undefined
	let bar: ImportedNode[] | undefined
	TOKEN.lastIndex = 0
	let match: RegExpExecArray | null
	while ((match = TOKEN.exec(html))) {
		const [, closing, tag = '', attrs = ''] = match
		const name = tag.toLowerCase()
		if (name === 'h3' && !closing) {
			pendingFolder = { title: textUntilTag(html, TOKEN.lastIndex), isBar: /PERSONAL_TOOLBAR_FOLDER\s*=\s*"true"/i.test(attrs) }
			continue
		}
		if (name === 'dl' && !closing) {
			if (stack.length === 0 && !pendingFolder) {
				stack.push({ folder: root, isBar: false })
				continue
			}
			const parent = stack.at(-1)?.folder ?? root
			const child = importedFolder(pendingFolder?.title ?? '', [])
			const isBar = pendingFolder?.isBar ?? false
			if (isBar) bar = child.children
			else parent.children.push(child)
			stack.push({ folder: child, isBar })
			pendingFolder = undefined
			continue
		}
		if (name === 'dl' && closing) {
			stack.pop()
			continue
		}
		if (name === 'a' && !closing) {
			const title = textUntilTag(html, TOKEN.lastIndex)
			const url = decodeEntities(/HREF\s*=\s*"([^"]*)"/i.exec(attrs)?.[1] ?? '')
			if (!acceptsBookmarkUrl(url)) {
				skipped.push(`${title || url}: only http(s) addresses can be bookmarks`)
				continue
			}
			const parent = stack.at(-1)?.folder ?? root
			parent.children.push(importedLink(title || url, url))
		}
	}
	return { bar: bar ?? [], other: unwrapSingleRoot(root.children), skipped }
}

// A title ends at the next tag, closing or not: exports write a "<" in a title
// as &lt;, and some never close their <A> and <H3> tags.
function textUntilTag(html: string, start: number): string {
	const end = html.indexOf('<', start)
	return decodeEntities(html.slice(start, end === -1 ? undefined : end).trim())
}

// Firefox and Safari wrap everything in one top-level folder ("Bookmarks
// Menu", "Favorites"); keep its contents rather than an extra empty level.
function unwrapSingleRoot(nodes: ImportedNode[]): ImportedNode[] {
	const only = nodes.length === 1 ? nodes[0] : undefined
	if (only?.kind === 'folder' && only.title === '') return only.children
	return nodes
}

const NAMED: Record<string, string> = { amp: '&', lt: '<', gt: '>', quot: '"', apos: "'", nbsp: ' ' }

export function decodeEntities(text: string): string {
	return text.replace(/&(#x[0-9a-f]+|#\d+|[a-z]+);/gi, (whole, body: string) => {
		if (body[0] === '#') {
			const code = body[1] === 'x' || body[1] === 'X' ? parseInt(body.slice(2), 16) : parseInt(body.slice(1), 10)
			return Number.isFinite(code) ? String.fromCodePoint(code) : whole
		}
		return NAMED[body.toLowerCase()] ?? whole
	})
}
