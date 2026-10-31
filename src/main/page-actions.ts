import { runInPage } from './web-contents'
import type { BrowserWindow, Session, WebContents } from 'electron'
import { DEFAULT_TEXT_SIZE, biggerText, smallerText, textSizeCss } from '@shared/browser/textSize'
import type { FindDirection, HistoryDirection } from '@shared/browser/pageUi'
import type { MenuAnchor } from '@shared/ipc'
import { DownloadManager, installDownloads, type DownloadOwner } from './downloads'
import { FindController } from './find'
import { popupHistoryMenu } from './history-menu'
import { PageUiLayer, type PageUiLayerOptions } from './pageui-layer'
import { PermissionBroker, installPermissions } from './permissions'
import { ReaderController } from './reader'

/**
 * Per-tab text size as an injected CSS override. insertCSS dies with the
 * document, so the override is re-inserted on every `dom-ready`; that is what
 * makes it persist across the tab's navigations.
 */
class TextSizeController {
	readonly #sizes = new WeakMap<WebContents, { percent: number; key: string | undefined }>()

	attach(contents: WebContents): void {
		contents.on('dom-ready', () => {
			void this.#insert(contents)
		})
	}

	percent(contents: WebContents): number {
		return this.#sizes.get(contents)?.percent ?? DEFAULT_TEXT_SIZE
	}

	async bigger(contents: WebContents): Promise<void> {
		await this.#set(contents, biggerText(this.percent(contents)))
	}

	async smaller(contents: WebContents): Promise<void> {
		await this.#set(contents, smallerText(this.percent(contents)))
	}

	async #set(contents: WebContents, percent: number): Promise<void> {
		const previous = this.#sizes.get(contents)
		if (previous?.key !== undefined) await contents.removeInsertedCSS(previous.key)
		this.#sizes.set(contents, { percent, key: undefined })
		await this.#insert(contents)
	}

	async #insert(contents: WebContents): Promise<void> {
		const size = this.#sizes.get(contents)
		if (!size || size.percent === DEFAULT_TEXT_SIZE || contents.isDestroyed()) return
		size.key = await contents.insertCSS(textSizeCss(size.percent))
	}
}

interface FullscreenOptions {
	readonly window: BrowserWindow
	readonly panels: PageUiLayer
	readonly relayout: () => void
}

/**
 * HTML fullscreen (a video's fullscreen button): the window goes fullscreen,
 * the page view takes all of it, and nothing floats over it. Layout reads
 * `active`; leaving restores the window to how it was.
 */
export class FullscreenController {
	#contents: WebContents | undefined
	#windowWasFullscreen = false

	constructor(private readonly options: FullscreenOptions) {
		options.window.on('leave-full-screen', () => this.#exitPage())
	}

	get active(): boolean {
		return this.#contents !== undefined
	}

	attach(contents: WebContents): void {
		contents.on('enter-html-full-screen', () => this.#enter(contents))
		contents.on('leave-html-full-screen', () => this.#leave(contents))
	}

	/** Switching tabs under a fullscreen video ends it. */
	activeChanged(active: WebContents | undefined): void {
		if (this.#contents && this.#contents !== active) this.#exitPage()
	}

	#enter(contents: WebContents): void {
		const { window } = this.options
		this.#contents = contents
		this.#windowWasFullscreen = window.isFullScreen()
		this.options.panels.setFullscreen(true)
		window.setFullScreen(true)
		this.options.relayout()
	}

	#leave(contents: WebContents): void {
		if (this.#contents !== contents) return
		this.#contents = undefined
		const { window } = this.options
		this.options.panels.setFullscreen(false)
		if (!this.#windowWasFullscreen && !window.isDestroyed() && window.isFullScreen()) window.setFullScreen(false)
		this.options.relayout()
	}

	#exitPage(): void {
		const contents = this.#contents
		if (!contents || contents.isDestroyed()) return
		void runInPage(contents, 'document.fullscreenElement && document.exitFullscreen()')
	}
}

export interface PageFeaturesOptions {
	readonly window: BrowserWindow
	readonly activeContents: () => WebContents | undefined
	readonly notify: (text: string) => void
	readonly relayout: () => void
	readonly panels: PageUiLayerOptions
}

/**
 * Everything a page can do for the user that isn't navigation: find, reader,
 * text size, print, downloads, permission prompts, fullscreen. One per window;
 * ZingWindow only forwards to it.
 */
export class PageFeatures implements DownloadOwner {
	readonly panels: PageUiLayer
	readonly downloads: DownloadManager
	readonly permissions: PermissionBroker
	readonly fullscreen: FullscreenController
	readonly #find: FindController
	readonly #reader = new ReaderController()
	readonly #textSize = new TextSizeController()
	readonly #pages = new Set<WebContents>()
	#lastActive: WebContents | undefined

	constructor(private readonly options: PageFeaturesOptions) {
		this.panels = new PageUiLayer(options.panels)
		this.downloads = new DownloadManager(this.panels, options.notify)
		this.permissions = new PermissionBroker(this.panels)
		this.#find = new FindController(this.panels, options.activeContents)
		this.fullscreen = new FullscreenController({ window: options.window, panels: this.panels, relayout: options.relayout })
	}

	/** Every page WebContents the window creates or adopts, popups included. */
	attach(contents: WebContents): void {
		this.#pages.add(contents)
		contents.once('destroyed', () => this.#pages.delete(contents))
		this.#find.attach(contents)
		this.#reader.attach(contents)
		this.#textSize.attach(contents)
		this.permissions.attach(contents)
		this.fullscreen.attach(contents)
	}

	owns(contents: WebContents): boolean {
		return this.#pages.has(contents)
	}

	referrerOf(contents: WebContents): string {
		return contents.getURL()
	}

	/** Called on every tab-state change; reacts only when the active page differs. */
	activeChanged(): void {
		const active = this.options.activeContents()
		if (active === this.#lastActive) return
		this.#lastActive = active
		this.#find.activeChanged()
		this.fullscreen.activeChanged(active)
	}

	find(): void {
		this.#find.open()
	}

	findStep(direction: FindDirection): void {
		this.#find.step(direction)
	}

	findUpdate(text: string): void {
		this.#find.update(text)
	}

	findClose(): void {
		this.#find.close()
	}

	showReader(contents: WebContents): void {
		this.#report('Reader', this.#reader.toggle(contents))
	}

	textBigger(contents: WebContents): void {
		this.#report('Text size', this.#textSize.bigger(contents))
	}

	textSmaller(contents: WebContents): void {
		this.#report('Text size', this.#textSize.smaller(contents))
	}

	print(contents: WebContents): void {
		contents.print({ printBackground: true })
	}

	popupHistoryMenu(direction: HistoryDirection, anchor: MenuAnchor): void {
		const contents = this.options.activeContents()
		if (contents) popupHistoryMenu(this.options.window, contents, direction, anchor)
	}

	raiseLayers(): void {
		this.panels.raise()
	}

	layout(): void {
		this.panels.layout()
	}

	destroy(): void {
		this.panels.destroy()
	}

	/** Command handlers are synchronous; a failure in the async part is shown, not dropped. */
	#report(what: string, work: Promise<void>): void {
		work.catch((error: unknown) => {
			this.options.notify(error instanceof Error ? error.message : `${what} failed (${String(error)})`)
		})
	}
}

/**
 * Downloads and permission prompts are session events, so one registration
 * routes each to the window that owns the page. A download with no owning page
 * goes to `fallback` (the most recent window) rather than being lost.
 */
export function installPageFeatures(
	session: Session,
	ownerOf: (contents: WebContents) => PageFeatures | undefined,
	fallback: () => PageFeatures | undefined,
): void {
	installDownloads(session, (contents) => (contents && ownerOf(contents)) ?? fallback())
	installPermissions(session, (contents) => (contents ? ownerOf(contents)?.permissions : undefined))
}
