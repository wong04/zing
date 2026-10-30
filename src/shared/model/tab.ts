export type TabId = string

/**
 * A tab as data. It exists with or without a live renderer, which is what lets
 * tabs sleep, restore lazily, sync and sit in a split later without a migration.
 */
export interface TabRecord {
	readonly id: TabId
	readonly url: string
	readonly title: string
	readonly faviconUrl?: string
	readonly pinned: boolean
	readonly createdAt: number
	readonly lastActiveAt: number
	readonly updatedAt: number
}

export type TabContentPatch = Partial<Pick<TabRecord, 'url' | 'title' | 'faviconUrl'>>

export const NEW_TAB_URL = 'about:blank'
export const NEW_TAB_TITLE = 'New Tab'

export function createTabRecord(url: string, now: number): TabRecord {
	return {
		id: crypto.randomUUID(),
		url,
		title: NEW_TAB_TITLE,
		pinned: false,
		createdAt: now,
		lastActiveAt: now,
		updatedAt: now,
	}
}

/** Worth offering back from Reopen Closed Tab; a blank new tab isn't. */
export function isRestorable(record: TabRecord): boolean {
	return /^https?:/i.test(record.url)
}
