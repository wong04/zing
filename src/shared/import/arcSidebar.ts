import { acceptsBookmarkUrl, importedFolder, importedLink, type BookmarkImport, type ImportedNode } from './importResult'

export class ArcSidebarFormatError extends Error {
	override name = 'ArcSidebarFormatError'
}

// Arc keeps its sidebar in StorableSidebar.json. On Windows that's
// %LOCALAPPDATA%\Packages\TheBrowserCompany.Arc_<id>\LocalCache\Local\Arc\.
// Spaces and items are stored as flat arrays that alternate an id string
// with the object it names: ["id1", {...}, "id2", {...}]. Each space lists
// its containers as ["pinned", "<containerId>", "unpinned", "<containerId>"],
// and items point at their children through `childrenIds`.
//
// This layout comes from community exporters, not a published spec, so every
// assumption fails loudly with ArcSidebarFormatError instead of importing
// half a sidebar.

interface ArcItem {
	id: string
	title?: string | null
	childrenIds?: string[]
	data?: {
		tab?: { savedTitle?: string; savedURL?: string }
		list?: object
		itemContainer?: object
	}
}

interface ArcSpace {
	id: string
	title?: string
	containerIDs?: string[]
}

interface ArcContainer {
	spaces?: unknown[]
	items?: unknown[]
	topAppsContainerIDs?: unknown[]
}

export function parseArcSidebar(json: string): BookmarkImport {
	const data: unknown = JSON.parse(json)
	const containers = (data as { sidebar?: { containers?: unknown[] } })?.sidebar?.containers
	if (!Array.isArray(containers)) throw new ArcSidebarFormatError('No sidebar.containers array')
	const main = containers.find((c): c is ArcContainer => typeof c === 'object' && c !== null && 'spaces' in c && 'items' in c)
	if (!main) throw new ArcSidebarFormatError('No container with spaces and items')
	const items = new Map(objectsOf<ArcItem>(main.items).map((item) => [item.id, item]))
	const spaces = objectsOf<ArcSpace>(main.spaces)
	const skipped: string[] = []
	const other: ImportedNode[] = []
	spaces.forEach((space, index) => {
		const pinnedId = containerAfter(space, 'pinned')
		if (!pinnedId) {
			skipped.push(`Space "${space.title ?? index + 1}" has no pinned container`)
			return
		}
		const pinned = items.get(pinnedId)
		const children = (pinned?.childrenIds ?? []).flatMap((id) => convert(id, items, skipped))
		other.push(importedFolder(space.title || `Space ${index + 1}`, children))
	})
	const bar = topApps(main, items, skipped)
	return { bar, other, skipped }
}

function objectsOf<T extends { id: string }>(list: unknown[] | undefined): T[] {
	if (!Array.isArray(list)) throw new ArcSidebarFormatError('Expected an array of alternating ids and objects')
	return list.filter((entry): entry is T => typeof entry === 'object' && entry !== null && typeof (entry as T).id === 'string')
}

function containerAfter(space: ArcSpace, label: string): string | undefined {
	const ids = space.containerIDs ?? []
	const at = ids.indexOf(label)
	if (at === -1) return undefined
	return ids[at + 1]
}

// Arc's Favorites (the icon grid at the top of the sidebar) are the closest
// thing to a Favourites bar.
function topApps(main: ArcContainer, items: Map<string, ArcItem>, skipped: string[]): ImportedNode[] {
	const ids = (main.topAppsContainerIDs ?? []).filter((id): id is string => typeof id === 'string')
	return ids.flatMap((containerId) => (items.get(containerId)?.childrenIds ?? []).flatMap((id) => convert(id, items, skipped)))
}

function convert(id: string, items: Map<string, ArcItem>, skipped: string[]): ImportedNode[] {
	const item = items.get(id)
	if (!item) {
		skipped.push(`Missing item ${id}`)
		return []
	}
	const tab = item.data?.tab
	if (tab) {
		const url = tab.savedURL ?? ''
		const title = item.title || tab.savedTitle || url
		if (!acceptsBookmarkUrl(url)) {
			skipped.push(`${title}: only http(s) addresses can be bookmarks`)
			return []
		}
		return [importedLink(title, url)]
	}
	if (item.data?.list || item.childrenIds) {
		return [importedFolder(item.title || 'Folder', (item.childrenIds ?? []).flatMap((child) => convert(child, items, skipped)))]
	}
	skipped.push(`Item ${id} is neither a tab nor a folder`)
	return []
}
