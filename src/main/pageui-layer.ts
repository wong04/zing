import { isViewLive, loadUnlessGone } from './web-contents'
import { WebContentsView, type BrowserWindow, type Rectangle, type WebContents } from 'electron'
import { EMPTY_PAGE_UI, hasVisiblePageUi, type PageUiState } from '@shared/browser/pageUi'

/** What the find bar, downloads list and permission prompt use to reach the screen. */
export interface PageUiHost {
	state(): PageUiState
	set(patch: Partial<PageUiState>): void
	/** Gives the panels renderer keyboard focus (the find input needs it). */
	focus(): void
}

export interface PageUiLayerOptions {
	readonly window: BrowserWindow
	readonly preload: string
	readonly load: (contents: WebContents) => Promise<void>
	readonly onContentsCreated: (contents: WebContents) => void
	/** Where the page starts below the chrome; it moves down when the Favourites bar shows. */
	readonly top: () => number
}

const PANEL_WIDTH = 392
const PANEL_MARGIN = 8

/**
 * A transparent column at the page's top-right. Like the notice overlay it is
 * sized to exactly what it shows (the renderer reports its height) and hidden
 * when empty, so it never swallows clicks meant for the page.
 */
export class PageUiLayer implements PageUiHost {
	#state: PageUiState = EMPTY_PAGE_UI
	#view: WebContentsView | undefined
	#ready: Promise<WebContentsView> | undefined
	#height = 0
	#fullscreen = false

	constructor(private readonly options: PageUiLayerOptions) {}

	state(): PageUiState {
		return this.#state
	}

	set(patch: Partial<PageUiState>): void {
		this.#state = { ...this.#state, ...patch }
		void this.#push()
	}

	focus(): void {
		void this.#ensure().then((view) => view.webContents.focus())
	}

	setHeight(height: number): void {
		this.#height = Math.max(0, Math.ceil(height))
		this.#sync()
	}

	/** Re-adding a child moves it to the top; called after a page view is attached. */
	raise(): void {
		if (this.#view) this.options.window.contentView.addChildView(this.#view)
	}

	layout(): void {
		this.#sync()
	}

	/** Fullscreen video owns the whole window, so nothing may float over it. */
	setFullscreen(fullscreen: boolean): void {
		this.#fullscreen = fullscreen
		this.#sync()
	}

	destroy(): void {
		if (this.#view && !this.#view.webContents.isDestroyed()) this.#view.webContents.close()
	}

	async #push(): Promise<void> {
		// Nothing to show and no view yet: don't pay for a renderer.
		if (!this.#view && !hasVisiblePageUi(this.#state)) return
		const view = await this.#ensure()
		if (!isViewLive(this.options.window, view)) return
		view.webContents.send('pageui:state', this.#state)
		this.#sync()
	}

	#sync(): void {
		const view = this.#view
		if (!view || view.webContents.isDestroyed()) return
		// Shown even before its height is known: a hidden view never lays out, so it could never report one.
		const shown = !this.#fullscreen && hasVisiblePageUi(this.#state)
		if (shown) view.setBounds(this.#bounds())
		view.setVisible(shown)
		if (shown) this.raise()
	}

	#bounds(): Rectangle {
		const { width, height } = this.options.window.getContentBounds()
		const w = Math.min(PANEL_WIDTH, width)
		const top = this.options.top()
		const available = Math.max(0, height - top - PANEL_MARGIN)
		return {
			x: width - w,
			y: top + PANEL_MARGIN,
			width: w,
			height: Math.max(1, Math.min(this.#height, available)),
		}
	}

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
		await loadUnlessGone(view.webContents, this.options.load)
		return view
	}
}
