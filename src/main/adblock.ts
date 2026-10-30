import { mkdir, readFile, rename, rm, stat, writeFile } from 'node:fs/promises'
import { dirname } from 'node:path'
import { ElectronBlocker, Request, type Fetch } from '@ghostery/adblocker-electron'
import type { Session } from 'electron'

export const BLOCKLIST_URLS: readonly string[] = [
	'https://easylist.to/easylist/easylist.txt',
	'https://easylist.to/easylist/easyprivacy.txt',
]

export const REFRESH_INTERVAL_MS = 24 * 60 * 60 * 1000
const RECHECK_INTERVAL_MS = 6 * 60 * 60 * 1000

// Cosmetic filters need a preload script in every page; Zing blocks requests only.
const ENGINE_CONFIG = { loadCosmeticFilters: false, loadNetworkFilters: true } as const

export class BlocklistFetchError extends Error {
	constructor(readonly url: string, readonly status: number) {
		super(`Could not download ${url} (HTTP ${status})`)
		this.name = 'BlocklistFetchError'
	}
}

export class CorruptEngineCacheError extends Error {
	constructor(readonly path: string, cause: unknown) {
		super(`The ad-block cache ${path} could not be read`, { cause })
		this.name = 'CorruptEngineCacheError'
	}
}

export interface BlockableRequest {
	readonly url: string
	readonly sourceUrl: string
	readonly type: 'mainFrame' | 'subFrame' | 'stylesheet' | 'script' | 'image' | 'font' | 'xhr' | 'ping' | 'media' | 'other'
}

/** A compiled engine from raw filter text, for tests and for the lists the refresh downloads. */
export function parseEngine(filterText: string): ElectronBlocker {
	return ElectronBlocker.parse(filterText, ENGINE_CONFIG)
}

export function engineBlocks(engine: ElectronBlocker, request: BlockableRequest): boolean {
	return engine.match(Request.fromRawDetails(request)).match
}

export interface AdBlockerOptions {
	/** The compiled engine lives here. */
	readonly cachePath: string
	readonly fetch: Fetch
	/** Read at request time, so flipping the setting takes effect without a restart. */
	readonly isEnabled: () => boolean
	readonly listUrls?: readonly string[]
	readonly now?: () => number
}

/**
 * EasyList + EasyPrivacy through Ghostery's engine. The compiled engine is
 * cached on disk so startup deserializes it in milliseconds instead of
 * parsing ~100k rules; lists refresh in the background at most once a day.
 *
 * The cache is not browsing history: it holds only the public filter lists,
 * never anything about which sites were visited or blocked.
 */
export class AdBlocker {
	readonly #options: AdBlockerOptions
	#engine: ElectronBlocker | undefined
	#refreshing: Promise<boolean> | undefined

	constructor(options: AdBlockerOptions) {
		this.#options = options
	}

	get ready(): boolean {
		return this.#engine !== undefined
	}

	/** Loads the cached engine. Resolves false when there is none yet. */
	async loadCache(): Promise<boolean> {
		const { cachePath } = this.#options
		let bytes: Buffer
		try {
			bytes = await readFile(cachePath)
		} catch (error) {
			if ((error as NodeJS.ErrnoException).code === 'ENOENT') return false
			throw error
		}
		try {
			this.#engine = ElectronBlocker.deserialize(new Uint8Array(bytes))
		} catch (error) {
			// Removed so the next refresh sees no cache instead of a "fresh" one it cannot read.
			await rm(cachePath, { force: true })
			throw new CorruptEngineCacheError(cachePath, error)
		}
		return true
	}

	/** Swaps in an engine built elsewhere (tests, or a refresh). Does not touch the disk. */
	useEngine(engine: ElectronBlocker): void {
		this.#engine = engine
	}

	shouldBlock(request: BlockableRequest): boolean {
		if (!this.#options.isEnabled() || !this.#engine) return false
		return engineBlocks(this.#engine, request)
	}

	/** Downloads and compiles the lists unless the cache is under a day old. Resolves true when it refreshed. */
	refreshIfStale(): Promise<boolean> {
		this.#refreshing ??= this.#refresh().finally(() => {
			this.#refreshing = undefined
		})
		return this.#refreshing
	}

	/** Checks for staleness on a timer; the first check is the caller's job (after first paint). */
	scheduleRefresh(onError: (error: unknown) => void): void {
		setInterval(() => this.refreshIfStale().catch(onError), RECHECK_INTERVAL_MS).unref()
	}

	/** Blocks matching requests in the session and, for main-frame loads, nothing else. */
	attach(session: Session): void {
		session.webRequest.onBeforeRequest((details, callback) => {
			const blocked = this.shouldBlock({
				url: details.url,
				sourceUrl: details.referrer,
				type: details.resourceType as BlockableRequest['type'],
			})
			callback(blocked ? { cancel: true } : {})
		})
	}

	async #refresh(): Promise<boolean> {
		if (await this.#cacheIsFresh()) return false
		const urls = this.#options.listUrls ?? BLOCKLIST_URLS
		const texts = await Promise.all(urls.map(async (url) => (await this.#options.fetch(url)).text()))
		const engine = parseEngine(texts.join('\n'))
		await this.#writeCache(engine.serialize())
		this.#engine = engine
		return true
	}

	async #cacheIsFresh(): Promise<boolean> {
		const now = (this.#options.now ?? Date.now)()
		try {
			const { mtimeMs } = await stat(this.#options.cachePath)
			return now - mtimeMs < REFRESH_INTERVAL_MS
		} catch (error) {
			if ((error as NodeJS.ErrnoException).code === 'ENOENT') return false
			throw error
		}
	}

	async #writeCache(bytes: Uint8Array): Promise<void> {
		const { cachePath } = this.#options
		await mkdir(dirname(cachePath), { recursive: true })
		// Write-then-rename so a crash mid-write never leaves a half-written engine to load.
		const partial = `${cachePath}.tmp`
		await writeFile(partial, bytes)
		await rename(partial, cachePath)
	}
}

/** Wraps a `Response`-returning fetch so a failed download is an error, not a list of HTML. */
export function strictFetch(fetchImpl: (url: string) => Promise<Response>): Fetch {
	return async (url) => {
		const response = await fetchImpl(url)
		if (!response.ok) throw new BlocklistFetchError(url, response.status)
		return response
	}
}
