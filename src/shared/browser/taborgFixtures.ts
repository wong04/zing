// Shared fixtures for the `Tests/taborg` port (tabOrganizer / tabLifecycle tests).
import { makeTabSnapshot, type TabSnapshot } from './tabOrganizer'

export const base = 1_700_000_000_000
const minuteMs = 60_000

export interface TabFixtureOptions {
	accessedMinutesAgo?: number
	createdMinutesAgo?: number
	pinned?: boolean
	active?: boolean
}

/** Like the Swift fixture, a tab's id is its name, so results read as names. */
export function tab(name: string, domain: string, options: TabFixtureOptions = {}): TabSnapshot {
	return makeTabSnapshot({
		id: name,
		title: name,
		domain,
		lastAccessed: base - (options.accessedMinutesAgo ?? 0) * minuteMs,
		createdAt: base - (options.createdMinutesAgo ?? 0) * minuteMs,
		isPinned: options.pinned ?? false,
		isActive: options.active ?? false,
	})
}

export const mixed: TabSnapshot[] = [
	tab('gmail', 'google.com', { createdMinutesAgo: 50 }),
	tab('bbc', 'bbc.co.uk', { createdMinutesAgo: 40 }),
	tab('docs', 'google.com', { createdMinutesAgo: 30 }),
	tab('apple', 'apple.com', { createdMinutesAgo: 20 }),
	tab('drive', 'google.com', { createdMinutesAgo: 10 }),
]
