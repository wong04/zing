import { WebContentsView, type BrowserWindow, type Rectangle, type WebContents } from 'electron'
import type { Notice } from '@shared/ipc'

export interface OverlayOptions {
	readonly window: BrowserWindow
	readonly preload: string
	readonly load: (contents: WebContents) => Promise<void>
	readonly onContentsCreated: (contents: WebContents) => void
}

const NOTICE_MS = 4000
const NOTICE_WIDTH = 480
const NOTICE_HEIGHT = 56
const NOTICE_MARGIN = 24

/**
 * The top layer. Page WebContentsViews paint above the chrome renderer, so
 * anything that floats over the page (menus, dropdowns, the find bar, notices)
 * is drawn in this transparent view instead. It is hidden whenever it shows
 * nothing, and when shown it is sized to what it shows, so it never takes
 * clicks meant for the page (Krackn's "SwiftUI over the page" gotcha).
 */
export class OverlayLayer {
	#view: WebContentsView | undefined
	#ready: Promise<WebContentsView> | undefined
	#hideTimer: ReturnType<typeof setTimeout> | undefined
	#nextNoticeId = 1

	constructor(private readonly options: OverlayOptions) {}

	/** Re-adding a child moves it to the top; called after a page view is attached. */
	raise(): void {
		if (this.#view) this.options.window.contentView.addChildView(this.#view)
	}

	async showNotice(text: string): Promise<void> {
		const view = await this.#ensure()
		const notice: Notice = { id: this.#nextNoticeId++, text }
		view.setBounds(this.#noticeBounds())
		view.webContents.send('overlay:notice', notice)
		this.raise()
		view.setVisible(true)
		clearTimeout(this.#hideTimer)
		this.#hideTimer = setTimeout(() => this.hide(), NOTICE_MS)
	}

	hide(): void {
		clearTimeout(this.#hideTimer)
		if (!this.#view) return
		this.#view.setVisible(false)
		this.#view.webContents.send('overlay:notice', null)
	}

	/** Keeps a visible notice anchored to the bottom of the page as the window resizes. */
	layout(): void {
		if (this.#view?.getVisible()) this.#view.setBounds(this.#noticeBounds())
	}

	destroy(): void {
		clearTimeout(this.#hideTimer)
		if (this.#view && !this.#view.webContents.isDestroyed()) this.#view.webContents.close()
	}

	#noticeBounds(): Rectangle {
		const { width, height } = this.options.window.getContentBounds()
		const w = Math.min(NOTICE_WIDTH, width - 2 * NOTICE_MARGIN)
		return {
			x: Math.round((width - w) / 2),
			y: height - NOTICE_HEIGHT - NOTICE_MARGIN,
			width: w,
			height: NOTICE_HEIGHT,
		}
	}

	/** Created on first use, so startup pays for one renderer, not two. */
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
