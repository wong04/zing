import type { MouseEvent } from 'react'
import type { TabView } from '@shared/ipc'
import type { BookmarkLink } from '@shared/library/library'
import { zing } from '../api'
import { TabIconView } from '../chrome/tabs/SlotChips'
import type { ItemDragHandlers } from './useItemDrag'

/** What the saved sections need from the rail: the tabs that belong to items, and the item drag. */
export interface SavedFrame {
	readonly tabsByItem: ReadonlyMap<string, TabView>
	readonly activeId: string | undefined
	readonly drag: (id: string) => ItemDragHandlers
	readonly endsDrag: () => boolean
}

export function itemMenu(id: string) {
	return (e: MouseEvent): void => {
		e.preventDefault()
		void zing.openLibraryItemMenu(id, { x: e.clientX, y: e.clientY })
	}
}

export function open(frame: SavedFrame, id: string): void {
	if (frame.endsDrag()) return
	void zing.openSavedTab(id)
}

/** A saved item's icon: its tab's (favicons live in memory only), or its first letter until the tab has loaded. */
export function ItemIcon({ link, tab }: { link: BookmarkLink; tab: TabView | undefined }) {
	if (tab) return <TabIconView tab={tab} />
	const letter = (link.title.trim()[0] ?? '?').toUpperCase()
	return (
		<span className="tab-icon rail-letter" aria-hidden="true">
			{letter}
		</span>
	)
}
