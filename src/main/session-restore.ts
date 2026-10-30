import { readFileSync, rmSync } from 'node:fs'
import { join } from 'node:path'
import type { TabId, TabRecord } from '@shared/model'
import { decodeSession, prepareSession, type SessionTab } from '@shared/session/session'
import type { SettingsStore } from '@shared/settings'
import { DebouncedJsonWriter } from './settings-file'

export class SessionFileError extends Error {
	constructor(
		readonly path: string,
		options: { cause: unknown },
	) {
		super(`Could not read the saved session at ${path}: ${(options.cause as Error).message}`, options)
		this.name = 'SessionFileError'
	}
}

export function sessionFilePath(userData: string): string {
	return join(userData, 'session.json')
}

/** The partition the browsing session lives in. Only the `persist:` prefix puts it on disk. */
export function browsingPartition(persistentProfile: boolean): string {
	return persistentProfile ? 'persist:zing' : 'zing-private'
}

/** No file is a first run; a corrupt one decodes to no tabs (Krackn's rule). An unreadable one is an error. */
export function loadSession(path: string): SessionTab[] {
	let text: string
	try {
		text = readFileSync(path, 'utf8')
	} catch (error) {
		if ((error as NodeJS.ErrnoException).code === 'ENOENT') return []
		throw new SessionFileError(path, { cause: error })
	}
	return decodeSession(text)
}

/** With restore off nothing may stay on disk, including a file left by an earlier run. */
export function discardSession(path: string): void {
	rmSync(path, { force: true })
}

export function toSessionTabs(records: readonly TabRecord[], activeId: TabId | undefined): SessionTab[] {
	return records.map((t) => ({ url: t.url, title: t.title, pinned: t.pinned, active: t.id === activeId }))
}

export interface SessionSource {
	readonly activeId: TabId | undefined
	readonly records: readonly TabRecord[]
}

/**
 * Writes the primary window's tabs to disk while `restoreTabs` is on. Writes are
 * debounced and atomic, and turning the setting off deletes the file.
 */
export class SessionSaver {
	readonly #source: SessionSource
	readonly #settings: Pick<SettingsStore, 'get' | 'subscribe'>
	readonly #writer: DebouncedJsonWriter
	readonly #unsubscribe: () => void

	constructor(path: string, source: SessionSource, settings: Pick<SettingsStore, 'get' | 'subscribe'>) {
		this.#source = source
		this.#settings = settings
		this.#writer = new DebouncedJsonWriter(path)
		this.#unsubscribe = settings.subscribe((_, key) => {
			if (key !== 'restoreTabs') return
			if (settings.get('restoreTabs')) this.changed()
			else this.#stop(path)
		})
	}

	/** Call whenever the window's tabs change. */
	changed(): void {
		if (!this.#settings.get('restoreTabs')) return
		this.#writer.schedule(prepareSession(toSessionTabs(this.#source.records, this.#source.activeId)))
	}

	/** On quit, so the last change inside the debounce window isn't lost. */
	flush(): void {
		this.#writer.flush()
	}

	dispose(): void {
		this.#unsubscribe()
	}

	#stop(path: string): void {
		// Flush first so no pending write can recreate the file after it is deleted.
		this.#writer.flush()
		discardSession(path)
	}
}

let pending: readonly SessionTab[] = []

/** Tabs the next window to open should come up with. Set once at launch, before the first window. */
export function queueRestore(tabs: readonly SessionTab[]): void {
	pending = tabs
}

/** Hands the queued tabs to exactly one window, so later windows open blank. */
export function takePendingRestore(): readonly SessionTab[] {
	const tabs = pending
	pending = []
	return tabs
}
