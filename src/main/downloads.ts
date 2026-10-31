import { existsSync, mkdirSync } from 'node:fs'
import { join } from 'node:path'
import { app, shell, type DownloadItem, type Session, type WebContents } from 'electron'
import { downloadFileName } from '@shared/browser/downloadName'
import type { DownloadEntry, DownloadStatus } from '@shared/browser/pageUi'
import { holdMarkOfTheWeb } from '../platform/windows/holdMark'
import { INTERNET_ZONE, readMarkOfTheWeb, writeMarkOfTheWeb } from '../platform/windows/security'
import type { PageUiHost } from './pageui-layer'

export class UnknownDownloadError extends Error {
	constructor(readonly id: number) {
		super(`No download with id ${id}`)
		this.name = 'UnknownDownloadError'
	}
}

const PROGRESS_THROTTLE_MS = 120
// How long Chromium may take to stamp its own mark on a busy PC.
const ENGINE_MARK_WINDOW_MS = 10_000
const ENGINE_MARK_POLL_MS = 100

/** `ZING_DOWNLOADS_DIR` lets tests save somewhere disposable. */
export function downloadsDirectory(): string {
	const override = process.env['ZING_DOWNLOADS_DIR']
	if (!override) return app.getPath('downloads')
	mkdirSync(override, { recursive: true })
	return override
}

function statusOf(state: 'completed' | 'cancelled' | 'interrupted'): DownloadStatus {
	if (state === 'completed') return 'completed'
	return state === 'cancelled' ? 'cancelled' : 'failed'
}

/** One window's downloads: the list the panel shows and the files behind it. */
export class DownloadManager {
	#entries: DownloadEntry[] = []
	readonly #items = new Map<number, DownloadItem>()
	readonly #paths = new Map<number, string>()
	// Names handed out but not yet on disk, so two simultaneous downloads of one name don't collide.
	readonly #reserved = new Set<string>()
	#nextId = 1
	#flush: ReturnType<typeof setTimeout> | undefined

	constructor(
		private readonly panels: PageUiHost,
		private readonly notify: (text: string) => void,
	) {}

	start(item: DownloadItem, referrer: string): void {
		const path = this.#claim(downloadsDirectory(), item.getFilename())
		item.setSavePath(path)
		const id = this.#nextId++
		this.#items.set(id, item)
		this.#paths.set(id, path)
		this.#entries = [
			...this.#entries,
			{ id, filename: item.getFilename(), status: 'progressing', received: 0, total: item.getTotalBytes() },
		]
		this.#publish()

		item.on('updated', () => {
			this.#update(id, { received: item.getReceivedBytes(), total: item.getTotalBytes() })
			this.#publishSoon()
		})
		item.once('done', (_event, state) => {
			this.#items.delete(id)
			this.#reserved.delete(path)
			this.#update(id, { status: statusOf(state), received: item.getReceivedBytes() })
			this.#publish()
			if (state === 'completed') void this.#mark(id, path, item, referrer)
		})
	}

	open(id: number): void {
		const path = this.#path(id)
		void shell.openPath(path).then((error) => {
			if (error) this.notify(`Couldn't open ${path} (${error})`)
		})
	}

	show(id: number): void {
		shell.showItemInFolder(this.#path(id))
	}

	cancel(id: number): void {
		this.#items.get(id)?.cancel()
	}

	clear(): void {
		this.#entries = this.#entries.filter((e) => e.status === 'progressing')
		this.panels.set({ downloads: this.#entries, downloadsOpen: this.#entries.length > 0 })
	}

	dismiss(): void {
		this.panels.set({ downloadsOpen: false })
	}

	#claim(directory: string, filename: string): string {
		const name = downloadFileName(filename, (candidate) => {
			const path = join(directory, candidate)
			return existsSync(path) || this.#reserved.has(path)
		})
		const path = join(directory, name)
		this.#reserved.add(path)
		return path
	}

	/**
	 * Without this stream SmartScreen and Office never treat the file as
	 * downloaded. Ours carries the real HostUrl and ReferrerUrl and is held
	 * against Chromium's later generic mark (see holdMarkOfTheWeb).
	 */
	async #mark(id: number, path: string, item: DownloadItem, referrer: string): Promise<void> {
		try {
			const ours = { zoneId: INTERNET_ZONE, hostUrl: item.getURL(), ...(referrer && { referrerUrl: referrer }) }
			await holdMarkOfTheWeb({ read: () => readMarkOfTheWeb(path), write: (mark) => writeMarkOfTheWeb(path, mark) }, ours, {
				windowMs: ENGINE_MARK_WINDOW_MS,
				pollMs: ENGINE_MARK_POLL_MS,
				sleep: (ms) => new Promise((resolve) => setTimeout(resolve, ms)),
			})
		} catch (error) {
			const reason = error instanceof Error ? error.message : String(error)
			this.#update(id, { error: `Saved, but couldn't mark it as downloaded (${reason})` })
			this.#publish()
		}
	}

	#path(id: number): string {
		const path = this.#paths.get(id)
		if (path === undefined) throw new UnknownDownloadError(id)
		return path
	}

	#update(id: number, patch: Partial<DownloadEntry>): void {
		this.#entries = this.#entries.map((e) => (e.id === id ? { ...e, ...patch } : e))
	}

	#publishSoon(): void {
		if (this.#flush) return
		this.#flush = setTimeout(() => this.#publish(), PROGRESS_THROTTLE_MS)
	}

	#publish(): void {
		clearTimeout(this.#flush)
		this.#flush = undefined
		this.panels.set({ downloads: this.#entries, downloadsOpen: true })
	}
}

export interface DownloadOwner {
	readonly downloads: DownloadManager
	/** The page that started the download: becomes the Mark of the Web's ReferrerUrl. */
	referrerOf(contents: WebContents): string
}

/**
 * Chromium raises `will-download` only for what `shouldDownload` would pick
 * (an attachment, or a type it can't show) plus explicit `<a download>`, so
 * every event here is saved; there is nothing left for the policy to veto.
 */
export function installDownloads(session: Session, ownerOf: (contents: WebContents | undefined) => DownloadOwner | undefined): void {
	session.on('will-download', (event, item, contents) => {
		const owner = ownerOf(contents)
		if (!owner) {
			event.preventDefault()
			return
		}
		owner.downloads.start(item, contents ? owner.referrerOf(contents) : '')
	})
}
