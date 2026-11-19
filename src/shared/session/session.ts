// The session file's pure codec. Chromium has no equivalent of WebKit's
// `interactionState`, so restored tabs keep URL, title, pinned and which tab
// was in front; the page reloads fresh.

/** One tab as it survives a quit. */
export interface SessionTab {
	readonly url: string
	readonly title: string
	readonly pinned: boolean
	readonly active: boolean
}

/** The session file is not a session: it is corrupt, so it must be kept aside rather than read as no tabs. */
export class SessionDecodeError extends Error {
	override name = 'SessionDecodeError'
}

/** A tab worth saving: real web pages only. A window of blank new tabs is worse than nothing. */
export function isRestorableUrl(url: string): boolean {
	if (!URL.canParse(url)) return false
	const { protocol } = new URL(url)
	return protocol === 'http:' || protocol === 'https:'
}

/** What is worth persisting: every web page, in order. Restored tabs other than the front one get no page, so their number costs little. */
export function prepareSession(tabs: readonly SessionTab[]): SessionTab[] {
	return tabs.filter((t) => isRestorableUrl(t.url))
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

/** A corrupt file throws SessionDecodeError, never decodes to no tabs: the caller keeps it aside and says so. */
export function decodeSession(text: string): SessionTab[] {
	let parsed: unknown
	try {
		parsed = JSON.parse(text)
	} catch (error) {
		throw new SessionDecodeError('The session file is not JSON', { cause: error })
	}
	if (!Array.isArray(parsed)) throw new SessionDecodeError('The session file is not a list of tabs')
	if (!parsed.every(isSessionTab)) throw new SessionDecodeError('The session file has an entry that is not a tab')
	return parsed.filter((t) => isRestorableUrl(t.url))
}
