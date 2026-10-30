import { WebContentsView, type BrowserWindow, type Rectangle, type WebContents } from 'electron'
import type { OverviewState, TabList } from '@shared/ipc'
import type { TabId } from '@shared/model'
import { overviewState, ThumbnailCache } from './thumbnails'

export interface TabOverviewOptions {
	readonly window: BrowserWindow
	readonly preload: string
	readonly load: (contents: WebContents) => Promise<void>
	readonly onContentsCreated: (contents: WebContents) => void
	readonly tabs: {
		state(): TabList
		contentsFor(id: TabId): WebContents | undefined
		idOf(contents: WebContents): TabId | undefined
	}
	/** Distance from the top of the window to the page area, which the overview covers. */
	readonly top: () => number
}

const CAPTURE_TIMEOUT_MS = 1500
const THUMBNAIL_WIDTH = 360
const JPEG_QUALITY = 70

class CaptureTimeoutError extends Error {
	constructor(readonly tabId: TabId) {
		super(`capturing tab ${tabId} took longer than ${CAPTURE_TIMEOUT_MS}ms`)
		this.name = 'CaptureTimeoutError'
	}
}

// A tab without a picture just shows its initial, but the reason is worth knowing.
function reportCaptureFailure(error: unknown): void {
	console.warn('tab overview capture failed:', error)
}

function withTimeout<T>(id: TabId, work: Promise<T>): Promise<T> {
	let timer: ReturnType<typeof setTimeout> | undefined
	const timeout = new Promise<never>((_resolve, reject) => {
		timer = setTimeout(() => reject(new CaptureTimeoutError(id)), CAPTURE_TIMEOUT_MS)
	})
	return Promise.race([work, timeout]).finally(() => clearTimeout(timer))
}

/**
 * The grid of tab pictures. Like the notice layer it is a transparent view
 * above the pages, because a page view paints over the chrome renderer.
 * Pictures are taken only when the overview opens, never in the background.
 */
export class TabOverview {
	readonly #thumbnails = new ThumbnailCache()
	#view: WebContentsView | undefined
	#ready: Promise<WebContentsView> | undefined
	#open = false
	readonly #leaving = new Set<Promise<void>>()

	constructor(private readonly options: TabOverviewOptions) {}

	get isOpen(): boolean {
		return this.#open
	}

	/** Null while closed, so a renderer that loads late can still ask. */
	current(): OverviewState | null {
		return this.#open ? this.state() : null
	}

	state(): OverviewState {
		return overviewState(this.options.tabs.state(), this.#thumbnails)
	}

	async toggle(): Promise<void> {
		if (this.#open) this.close()
		else await this.open()
	}

	async open(): Promise<void> {
		if (this.#open) return
		this.#open = true
		// Pictures of tabs that just left the screen may still be on their way.
		await Promise.all([...this.#leaving, this.#captureActive()])
		const view = await this.#ensure()
		// Closed again while the pictures were being taken.
		if (!this.#open) return
		view.setBounds(this.#bounds())
		this.#push()
		this.options.window.contentView.addChildView(view)
		view.setVisible(true)
		// Escape and the arrow keys go to the grid, not the page underneath.
		view.webContents.focus()
	}

	close(): void {
		if (!this.#open) return
		this.#open = false
		if (!this.#view || this.#view.webContents.isDestroyed()) return
		this.#view.setVisible(false)
		this.#view.webContents.send('overlay:overview', null)
		this.options.tabs.contentsFor(this.options.tabs.state().activeId ?? '')?.focus()
	}

	/** Tabs came or went while open: redraw from what is already captured. */
	refresh(): void {
		if (this.#open && this.#view) this.#push()
	}

	/**
	 * Called just before a page view leaves the screen: a page nobody can see
	 * produces no frames, so this is the last chance for its picture.
	 */
	rememberLeaving(contents: WebContents): void {
		const id = this.options.tabs.idOf(contents)
		if (id === undefined) return
		const pending = this.#capture(id)
			.catch(reportCaptureFailure)
			.finally(() => this.#leaving.delete(pending))
		this.#leaving.add(pending)
	}

	forget(id: TabId): void {
		this.#thumbnails.delete(id)
	}

	/** Re-adding a child moves it to the top; called after a page view is attached. */
	raise(): void {
		if (this.#open && this.#view) this.options.window.contentView.addChildView(this.#view)
	}

	layout(): void {
		if (this.#open && this.#view) this.#view.setBounds(this.#bounds())
	}

	destroy(): void {
		if (this.#view && !this.#view.webContents.isDestroyed()) this.#view.webContents.close()
	}

	#push(): void {
		this.#view?.webContents.send('overlay:overview', this.state())
	}

	#bounds(): Rectangle {
		const { width, height } = this.options.window.getContentBounds()
		const top = this.options.top()
		return { x: 0, y: top, width, height: Math.max(0, height - top) }
	}

	/** Only the page on screen can be captured now; the others were captured as they left it. */
	async #captureActive(): Promise<void> {
		const id = this.options.tabs.state().activeId
		if (id === undefined) return
		await this.#capture(id).catch(reportCaptureFailure)
	}

	async #capture(id: TabId): Promise<void> {
		const contents = this.options.tabs.contentsFor(id)
		if (!contents || contents.isDestroyed()) return
		const image = await withTimeout(id, contents.capturePage())
		if (image.isEmpty()) return
		const small = image.resize({ width: THUMBNAIL_WIDTH, quality: 'good' })
		this.#thumbnails.set(id, `data:image/jpeg;base64,${small.toJPEG(JPEG_QUALITY).toString('base64')}`)
	}

	/** Created on first use, so a window that never opens the overview pays nothing. */
	#ensure(): Promise<WebContentsView> {
		this.#ready ??= this.#create()
		return this.#ready
	}

	async #create(): Promise<WebContentsView> {
		const view = new WebContentsView({
			webPreferences: {
				preload: this.options.preload,
				sandbox: true,
				contextIsolation: true,
				nodeIntegration: false,
				partition: 'zing-chrome',
			},
		})
		view.setBackgroundColor('#00000000')
		view.setVisible(false)
		this.options.window.contentView.addChildView(view)
		this.options.onContentsCreated(view.webContents)
		this.#view = view
		await this.options.load(view.webContents)
		return view
	}
}
