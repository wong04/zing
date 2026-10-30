// Port of Krackn's `SessionStore` pure codec (Features/Browser/SessionStore.swift).
// WebKit's `interactionState` has no Chromium equivalent, so restored tabs keep
// URL, title, pinned and which tab was in front; the page reloads fresh.

/** One tab as it survives a quit. */
export interface SessionTab {
	readonly url: string
	readonly title: string
	readonly pinned: boolean
	readonly active: boolean
}

/** Beyond this the file stops being a convenience and becomes a liability, in size and in recorded history. */
export const MAX_SESSION_TABS = 40

/** A tab worth saving: real web pages only. A window of blank new tabs is worse than nothing. */
export function isRestorableUrl(url: string): boolean {
	if (!URL.canParse(url)) return false
	const { protocol } = new URL(url)
	return protocol === 'http:' || protocol === 'https:'
}

/** What is worth persisting. Pinned tabs survive the cap first: they are the ones explicitly kept. */
export function prepareSession(tabs: readonly SessionTab[]): SessionTab[] {
	const kept = tabs.filter((t) => isRestorableUrl(t.url))
	if (kept.length <= MAX_SESSION_TABS) return kept
	const pinned = kept.filter((t) => t.pinned)
	const loose = kept.filter((t) => !t.pinned)
	return [...pinned, ...loose].slice(0, MAX_SESSION_TABS)
}

export function encodeSession(tabs: readonly SessionTab[]): string {
	return JSON.stringify(prepareSession(tabs))
}

function isSessionTab(value: unknown): value is SessionTab {
	if (typeof value !== 'object' || value === null) return false
	const tab = value as Record<string, unknown>
	return (
		typeof tab.url === 'string' &&
		typeof tab.title === 'string' &&
		typeof tab.pinned === 'boolean' &&
		typeof tab.active === 'boolean'
	)
}

/**
 * Krackn's spec: a corrupt or half-written session file must never stop the
 * browser opening, so anything unreadable decodes to no tabs.
 */
export function decodeSession(text: string): SessionTab[] {
	let parsed: unknown
	try {
		parsed = JSON.parse(text)
	} catch {
		return []
	}
	if (!Array.isArray(parsed)) return []
	return parsed.filter(isSessionTab).filter((t) => isRestorableUrl(t.url))
}
