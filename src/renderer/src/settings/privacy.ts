import type { Settings } from '@shared/settings'

/**
 * One way Zing can end up keeping something on disk. The privacy banner is
 * green only while none is active, and names every one that is: a privacy claim
 * that stays cheerful while a switch quietly contradicts it is worse than none
 * (Krackn: "Don't tidy that banner back to always-green").
 */
export interface StoresDataCondition {
	readonly id: string
	/** Read on every render, so it may consult settings or any other state its feature owns. */
	readonly active: (settings: Settings) => boolean
	/** Names what is kept, lowercase, to be joined into a sentence: "cookies and cache". */
	readonly what: string
	/** True when `what` reads as a plural ("cookies and cache", "saved passwords"): "are written", not "is". */
	readonly plural?: boolean
}

const conditions: StoresDataCondition[] = [
	{ id: 'persistentProfile', active: (s) => s.persistentProfile, what: 'cookies and cache', plural: true },
	{ id: 'restoreTabs', active: (s) => s.restoreTabs, what: 'your open tabs', plural: true },
]

export class DuplicateConditionError extends Error {
	constructor(readonly id: string) {
		super(`A stores-data condition named "${id}" is already registered`)
		this.name = 'DuplicateConditionError'
	}
}

/**
 * For features that store data (the password vault, imported bookmarks, sync).
 * Returns the function that removes it again.
 */
export function registerStoresDataCondition(condition: StoresDataCondition): () => void {
	if (conditions.some((c) => c.id === condition.id)) throw new DuplicateConditionError(condition.id)
	conditions.push(condition)
	return () => {
		const index = conditions.indexOf(condition)
		if (index !== -1) conditions.splice(index, 1)
	}
}

export interface PrivacyStatus {
	/** Nothing is being kept. */
	readonly traceless: boolean
	readonly headline: string
	readonly detail: string
}

function joinWords(parts: readonly string[]): string {
	if (parts.length <= 1) return parts.join('')
	return `${parts.slice(0, -1).join(', ')} and ${parts.at(-1)}`
}

function capitalized(text: string): string {
	return text.charAt(0).toUpperCase() + text.slice(1)
}

export function privacyStatus(settings: Settings): PrivacyStatus {
	const active = conditions.filter((c) => c.active(settings))
	const kept = active.map((c) => c.what)
	if (kept.length === 0) {
		return {
			traceless: true,
			headline: 'Browsing leaves nothing on this PC',
			detail: 'Cookies, cache and history live in memory and vanish on quit.',
		}
	}
	return {
		traceless: false,
		headline: 'Some browsing data is saved on this PC',
		detail: `${capitalized(joinWords(kept))} ${kept.length === 1 && active[0]?.plural !== true ? 'is' : 'are'} written to disk.`,
	}
}
