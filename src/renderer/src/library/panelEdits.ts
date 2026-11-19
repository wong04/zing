import type { LibraryAction, LibraryPanel } from '@shared/ipc'
import { find, isFolder, removalCount, type BookmarkNode, type BrowserLibrary } from '@shared/library/library'

interface Place {
	/** The folder holding the node; undefined at the top level. */
	readonly parent: string | undefined
	readonly index: number
}

function placeOf(nodes: BookmarkNode[], id: string, parent?: string): Place | undefined {
	const index = nodes.findIndex((n) => n.id === id)
	if (index >= 0) return { parent, index }
	for (const node of nodes) {
		if (!isFolder(node)) continue
		const hit = placeOf(node.children, id, node.id)
		if (hit) return hit
	}
	return undefined
}

/** In one list the dragged row takes the place of the row it is dropped on; `to` is an insertion point before the move. */
const intoPlaceOf = (from: number, at: number): number => (from < at ? at + 1 : at)

/** The edit a drop of `dragged` on the row `target` makes, or undefined when it can't go there. */
export function dropAction(library: BrowserLibrary, panel: LibraryPanel, dragged: string, target: string): LibraryAction | undefined {
	if (dragged === target) return undefined
	if (panel === 'readingList') {
		const from = library.readingList.findIndex((i) => i.id === dragged)
		const at = library.readingList.findIndex((i) => i.id === target)
		return from < 0 || at < 0 ? undefined : { type: 'move', id: dragged, to: intoPlaceOf(from, at) }
	}
	const from = placeOf(library.bookmarks, dragged)
	const at = placeOf(library.bookmarks, target)
	if (!from || !at) return undefined
	if (from.parent === at.parent) return { type: 'move', id: dragged, to: intoPlaceOf(from.index, at.index) }
	if (at.parent === undefined) return undefined
	const moving = find(dragged, library.bookmarks)
	if (moving && isFolder(moving) && find(at.parent, [moving])) return undefined
	return { type: 'moveInto', id: dragged, parent: at.parent, to: at.index }
}

const counted = (n: number, noun: string): string => `${n} ${noun}${n === 1 ? '' : 's'}`

/** What to ask before deleting these rows, or undefined when only one thing goes and it goes at once. */
export function removalPrompt(library: BrowserLibrary, panel: LibraryPanel, ids: readonly string[]): string | undefined {
	if (panel === 'readingList') return ids.length > 1 ? `Delete ${counted(ids.length, 'Reading List item')}?` : undefined
	const { bookmarks, folders } = removalCount(new Set(ids), library.bookmarks)
	if (bookmarks + folders <= 1) return undefined
	return `Delete ${counted(bookmarks, 'bookmark')}${folders > 0 ? ` and ${counted(folders, 'folder')}` : ''}?`
}
