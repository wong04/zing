import { Menu, type BrowserWindow, type WebContents } from 'electron'
import type { HistoryDirection } from '@shared/browser/pageUi'
import type { MenuAnchor } from '@shared/ipc'

export interface HistoryEntry {
	readonly url: string
	readonly title: string
}

export interface HistoryMenuItem {
	/** Index into the tab's navigation history, what `goToIndex` takes. */
	readonly index: number
	readonly label: string
}

const MENU_LIMIT = 15
const LABEL_LIMIT = 60

function labelOf(entry: HistoryEntry): string {
	const text = entry.title.trim() || entry.url
	return text.length > LABEL_LIMIT ? `${text.slice(0, LABEL_LIMIT - 1)}…` : text
}

/** Nearest entry first: back walks toward the start, forward toward the end. */
export function historyMenuItems(
	entries: readonly HistoryEntry[],
	activeIndex: number,
	direction: HistoryDirection,
	limit = MENU_LIMIT,
): HistoryMenuItem[] {
	const indices =
		direction === 'back'
			? Array.from({ length: Math.max(0, activeIndex) }, (_, i) => activeIndex - 1 - i)
			: Array.from({ length: Math.max(0, entries.length - activeIndex - 1) }, (_, i) => activeIndex + 1 + i)
	return indices.slice(0, limit).flatMap((index) => {
		const entry = entries[index]
		return entry ? [{ index, label: labelOf(entry) }] : []
	})
}

export function popupHistoryMenu(
	window: BrowserWindow,
	contents: WebContents,
	direction: HistoryDirection,
	anchor: MenuAnchor,
): void {
	const { navigationHistory } = contents
	const items = historyMenuItems(navigationHistory.getAllEntries(), navigationHistory.getActiveIndex(), direction)
	if (items.length === 0) return
	const menu = Menu.buildFromTemplate(
		items.map((item) => ({ label: item.label, click: () => navigationHistory.goToIndex(item.index) })),
	)
	menu.popup({ window, x: Math.round(anchor.x), y: Math.round(anchor.y) })
}
