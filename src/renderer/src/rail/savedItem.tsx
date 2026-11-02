import type { HTMLAttributes, MouseEvent } from 'react'
import type { MenuAnchor, TabView } from '@shared/ipc'
import type { BookmarkFolder, BookmarkLink } from '@shared/library/library'
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

/** The props a saved row shares, folder or item: a listitem a press drags, a click or Enter or Space opens, and a right click menus. */
function savedRowProps(frame: SavedFrame, id: string, title: string, className: string, activate: (at: MenuAnchor) => void): HTMLAttributes<HTMLElement> {
	return {
		role: 'listitem',
		tabIndex: 0,
		'aria-label': title,
		title,
		className,
		...frame.drag(id),
		onClick: (e) => {
			if (!frame.endsDrag()) activate({ x: e.clientX, y: e.clientY })
		},
		onKeyDown: (e) => {
			if (e.key !== 'Enter' && e.key !== ' ') return
			// Space would otherwise scroll the rail.
			e.preventDefault()
			const rect = e.currentTarget.getBoundingClientRect()
			activate({ x: rect.left, y: rect.bottom })
		},
		onContextMenu: itemMenu(id),
	}
}

/** A folder row's props: opening it shows its menu at the anchor. */
export function savedFolderProps(frame: SavedFrame, folder: BookmarkFolder, className: string, openMenu: (at: MenuAnchor) => void): HTMLAttributes<HTMLElement> {
	return savedRowProps(frame, folder.id, folder.title, className, openMenu)
}

/** An item row's props: its tab's state as classes and aria-current; opening it runs `openItem`. */
export function savedItemProps(frame: SavedFrame, link: BookmarkLink, base: string, openItem: () => void): HTMLAttributes<HTMLElement> {
	const tab = frame.tabsByItem.get(link.id)
	const active = tab !== undefined && tab.id === frame.activeId
	const className = [base, active && 'active', !tab && 'unloaded'].filter(Boolean).join(' ')
	return {
		...savedRowProps(frame, link.id, link.title, className, openItem),
		'aria-current': active ? 'page' : undefined,
	}
}
