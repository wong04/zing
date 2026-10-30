// Memory-only site icons. A favicon cache on disk is a dated list of every
// site visited, so this module deliberately imports nothing that can write one.

/** Resolves to a data URL for the first candidate that is a usable image, or null when none is. */
export type FetchIcon = (candidates: readonly string[]) => Promise<string | null>

const iconKey = (iconURL: string): string => `icon:${iconURL}`
const hostKey = (host: string): string => `host:${host.toLowerCase()}`

/** The conventional places for a site that declares no icon. */
export function conventionalIconUrls(host: string): string[] {
	return [`https://${host}/favicon.ico`, `https://${host}/apple-touch-icon.png`, `https://${host}/favicon.png`]
}

/**
 * Keyed by the icon's own address when the page declares one, and by the
 * full host otherwise, never by registrable domain: that made every Google
 * tab wear whichever Google icon loaded first. A null value records "looked,
 * there isn't one" so a miss is not fetched again on every state push.
 * Least recently used entries go first once the cap is reached.
 */
export class FaviconStore {
	readonly #entries = new Map<string, string | null>()
	readonly #inFlight = new Set<string>()

	constructor(
		private readonly fetchIcon: FetchIcon,
		private readonly onChange: () => void,
		private readonly capacity = 256,
	) {}

	get size(): number {
		return this.#entries.size
	}

	/** The page's own icon if it declared one and it has loaded, otherwise the host's conventional one. */
	icon(iconURL: string | undefined, host: string): string | undefined {
		return (iconURL ? this.#recall(iconKey(iconURL)) : undefined) ?? (host ? this.#recall(hostKey(host)) : undefined)
	}

	loadIcon(iconURL: string): Promise<void> {
		return this.#loadOnce(iconKey(iconURL), [iconURL])
	}

	loadHost(host: string): Promise<void> {
		if (!host) return Promise.resolve()
		return this.#loadOnce(hostKey(host), conventionalIconUrls(host))
	}

	#recall(key: string): string | undefined {
		const value = this.#entries.get(key)
		if (value === undefined) return undefined
		// Re-insert so the entry counts as recently used.
		this.#entries.delete(key)
		this.#entries.set(key, value)
		return value ?? undefined
	}

	async #loadOnce(key: string, candidates: readonly string[]): Promise<void> {
		if (this.#entries.has(key) || this.#inFlight.has(key)) return
		this.#inFlight.add(key)
		try {
			this.#remember(key, await this.fetchIcon(candidates))
		} finally {
			this.#inFlight.delete(key)
		}
		this.onChange()
	}

	#remember(key: string, value: string | null): void {
		this.#entries.set(key, value)
		while (this.#entries.size > this.capacity) {
			const oldest = this.#entries.keys().next()
			if (oldest.done) return
			this.#entries.delete(oldest.value)
		}
	}
}
