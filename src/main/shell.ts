import { cursorPoint } from './test-hooks'
import { isViewLive, loadUnlessGone } from './web-contents'
import { Menu, WebContentsView, type BrowserWindow, type Session, type WebContents } from 'electron'
import { type EventChannel, type EventContract, type MenuAnchor } from '@shared/ipc'
import { isSettingKey, UnknownSettingError, type Settings, type SettingsStore } from '@shared/settings'
import type { Rect } from '@shared/shell/panelLayout'
import { touching, removing } from '@shared/shell/recentList'
import {
	SIDEBAR_COLUMN_WIDTH,
	type AccountOutcome,
	type OpenPanelOutcome,
	type Point,
	type SettingsCategoryId,
	type ShellMenuKind,
	type ShellState,
} from '@shared/shell/shellState'
import { panelContentFromId, Section, type PanelContent } from '@shared/toolbox/panelContent'
import { AccountError, type AccountStore } from './account'
import { PanelStore, surfaceRegion } from './panels'
import { EdgeReveal } from './sidebar'

export class UnknownPanelContentError extends Error {
	constructor(readonly id: string) {
		super(`${id} names no panel content`)
		this.name = 'UnknownPanelContentError'
	}
}

export interface ShellOptions {
	readonly window: BrowserWindow
	readonly settings: SettingsStore
	readonly account: AccountStore
	readonly session: Session
	readonly preload: string
	readonly load: (contents: WebContents) => Promise<void>
	readonly onContentsCreated: (contents: WebContents) => void
	readonly appVersion: string
	/** Gives keyboard focus back to the page after a sheet closes. */
	readonly focusPage: () => void
	readonly notify: (text: string) => void
	/** The page view's rectangle changed (the sidebar was pinned or unpinned). */
	readonly relayout: () => void
	/** The page view's rectangle, owned by the window: it knows the tab bar position, Favourites bar and docked panels. */
	readonly pageArea: () => Rect
	readonly clock?: () => number
}

const POLL_MS = 50
const DEFAULT_CATEGORY: SettingsCategoryId = 'general'

/**
 * Everything the app shell draws that is not the page: the sidebar, the
 * settings sheet, the floating tool panels and the account gate. The pinned
 * sidebar lives in the chrome renderer and takes layout space from the page;
 * everything that floats over the page is drawn in the surface view, which is
 * hidden when it shows nothing and sized to what it shows (see surfaceRegion).
 */
export class ShellController {
	readonly #o: ShellOptions
	readonly #panels = new PanelStore()
	readonly #reveal: EdgeReveal
	readonly #clock: () => number
	readonly #unsubscribe: (() => void)[] = []
	#view: WebContentsView | undefined
	#ready: Promise<WebContentsView> | undefined
	#loaded = false
	#timer: ReturnType<typeof setInterval> | undefined
	#settingsOpen = false
	#category: SettingsCategoryId = DEFAULT_CATEGORY
	#region: Rect | null = null

	constructor(options: ShellOptions) {
		this.#o = options
		this.#clock = options.clock ?? Date.now
		this.#reveal = new EdgeReveal({
			onChange: () => {
				this.#sync()
				this.#sendState()
			},
		})
		this.#unsubscribe.push(
			options.settings.subscribe((settings, key) => {
				this.#send('settings:changed', settings)
				if (key === 'sidebarPinned') this.#onPinnedChange(settings.sidebarPinned)
			}),
			options.account.subscribe((state) => {
				this.#send('account:state', state)
				this.#sync()
				this.#sendState()
				this.#focusSurfaceIfCovering()
			}),
		)
		this.#applyPinned(options.settings.get('sidebarPinned'))
		if (this.gated) {
			this.#sync()
			this.#focusSurfaceIfCovering()
		}
	}

	get gated(): boolean {
		return !this.#o.account.state().signedIn
	}

	/** The column a pinned sidebar takes from the chrome; zero when it is hidden. */
	get pinnedWidth(): number {
		return this.#o.settings.get('sidebarPinned') ? SIDEBAR_COLUMN_WIDTH : 0
	}

	/** Where the page view sits, in whole DIPs. */
	pageRect(): Rect {
		return this.#o.pageArea()
	}

	state(): ShellState {
		return {
			settingsOpen: this.#settingsOpen,
			settingsCategory: this.#category,
			sidebarRevealed: this.#reveal.revealed,
			gated: this.gated,
			origin: { x: this.#region?.x ?? 0, y: this.#region?.y ?? 0 },
			appVersion: this.#o.appVersion,
		}
	}

	toolPanels() {
		return this.#panels.state()
	}

	settingsSnapshot(): Settings {
		return this.#o.settings.snapshot()
	}

	/** The store validates the value; an unknown key is the caller's bug and is named as one. */
	setSetting(key: string, value: unknown): void {
		if (!isSettingKey(key)) throw new UnknownSettingError(key)
		this.#o.settings.set(key, value as never)
	}

	/** Re-adding a child moves it to the top; called after a page view is attached. */
	raise(): void {
		if (this.#view) this.#o.window.contentView.addChildView(this.#view)
	}

	/** After the window resizes or the page rectangle moves. */
	layout(): void {
		this.#panels.clampAll(this.pageRect())
		this.#sync()
		this.#sendPanels()
	}

	destroy(): void {
		clearInterval(this.#timer)
		for (const off of this.#unsubscribe) off()
		if (this.#view && !this.#view.webContents.isDestroyed()) this.#view.webContents.close()
	}

	// MARK: Settings sheet

	openSettings(category?: SettingsCategoryId): void {
		if (category) this.#category = category
		this.#settingsOpen = true
		this.#sync()
		this.#sendState()
		void this.#ensure().then((view) => {
			if (isViewLive(this.#o.window, view)) view.webContents.focus()
		})
	}

	closeSettings(): void {
		if (!this.#settingsOpen) return
		this.#settingsOpen = false
		this.#sync()
		this.#sendState()
		this.#o.focusPage()
	}

	// MARK: Sidebar

	toggleSidebar(): void {
		this.#o.settings.set('sidebarPinned', !this.#o.settings.get('sidebarPinned'))
	}

	openToolbox(): void {
		this.#o.notify("The Toolbox isn't implemented yet")
	}

	// MARK: Tool panels

	/** `screenPoint` is where the tear-off was released, in screen coordinates. */
	openPanel(contentId: string, screenPoint: Point): OpenPanelOutcome {
		const content = this.#content(contentId)
		const at = this.#toWindow(screenPoint)
		const outcome = this.#panels.open(content, at, this.pageRect())
		return this.#afterOpen(contentId, outcome)
	}

	/** From a menu: lands in the middle of the page rather than under a pointer. */
	openPanelCentred(contentId: string): OpenPanelOutcome {
		const content = this.#content(contentId)
		const page = this.pageRect()
		const at = { x: page.x + page.width / 2, y: page.y + page.height / 3 }
		return this.#afterOpen(contentId, this.#panels.open(content, at, page))
	}

	closePanel(id: string): void {
		this.#panels.close(id)
		this.#panelsChanged()
	}

	minimizePanel(id: string): void {
		this.#panels.minimize(id)
		this.#panelsChanged()
	}

	restorePanel(id: string): OpenPanelOutcome {
		const outcome = this.#panels.restore(id, this.pageRect())
		return this.#afterOpen(undefined, outcome)
	}

	focusPanel(id: string): void {
		this.#panels.bringToFront(id)
		this.#sendPanels()
	}

	beginPanelDrag(id: string): void {
		this.#panels.beginDrag(id)
		this.#panelsChanged()
	}

	endPanelDrag(id: string, origin: Point): void {
		this.#panels.endDrag(id, origin, this.pageRect())
		this.#panelsChanged()
	}

	popupMenu(kind: ShellMenuKind, id: string, anchor: MenuAnchor): void {
		const template =
			kind === 'recent'
				? [
						{ label: 'Open in Window', click: () => this.openPanelCentred(id) },
						{ type: 'separator' as const },
						{ label: 'Remove from Sidebar', click: () => this.#forgetRecent(id) },
					]
				: [
						{ label: 'Restore', click: () => this.restorePanel(id) },
						{ label: 'Close', click: () => this.closePanel(id) },
					]
		Menu.buildFromTemplate(template).popup({ window: this.#o.window, x: Math.round(anchor.x), y: Math.round(anchor.y) })
	}

	// MARK: Privacy

	async clearBrowsingSession(): Promise<void> {
		await Promise.all([this.#o.session.clearStorageData(), this.#o.session.clearCache(), this.#o.session.clearAuthCache()])
		this.#o.notify('Browsing session cleared')
	}

	// MARK: Account

	/** Maps the store's expected failures to a result; anything else is a bug and propagates. */
	accountAction(run: () => void): AccountOutcome {
		try {
			run()
		} catch (error) {
			if (!(error instanceof AccountError)) throw error
			return { status: 'rejected', code: error.code, message: error.message }
		}
		return { status: 'ok', state: this.#o.account.state() }
	}

	// MARK: Internals

	#content(id: string): PanelContent {
		const content = panelContentFromId(id)
		if (!content) throw new UnknownPanelContentError(id)
		return content
	}

	#afterOpen(contentId: string | undefined, outcome: OpenPanelOutcome): OpenPanelOutcome {
		if (outcome.status === 'full') {
			this.#o.notify('Three panels are open. Minimize or close one first')
			return outcome
		}
		if (contentId) this.#remember(contentId)
		this.#panelsChanged()
		return outcome
	}

	#remember(contentId: string): void {
		// The Settings section is never a tool; only features go in the list.
		if (contentId === `section:${Section.settings}`) return
		this.#o.settings.set('recentFeatures', touching(contentId, this.#o.settings.get('recentFeatures')))
	}

	#forgetRecent(id: string): void {
		this.#o.settings.set('recentFeatures', removing(id, this.#o.settings.get('recentFeatures')))
	}

	#toWindow(screenPoint: Point): Point {
		const content = this.#o.window.getContentBounds()
		return { x: screenPoint.x - content.x, y: screenPoint.y - content.y }
	}

	#panelsChanged(): void {
		this.#sync()
		this.#sendState()
		this.#sendPanels()
	}

	#onPinnedChange(pinned: boolean): void {
		this.#applyPinned(pinned)
		// The page view's left edge moves with the sidebar.
		this.#o.relayout()
		if (!pinned) this.#reveal.peek(this.#clock())
	}

	#applyPinned(pinned: boolean): void {
		this.#reveal.setEnabled(!pinned)
		clearInterval(this.#timer)
		this.#timer = undefined
		if (pinned) return
		this.#timer = setInterval(() => this.#sampleCursor(), POLL_MS)
	}

	#sampleCursor(): void {
		const { window } = this.#o
		if (window.isDestroyed() || !window.isFocused()) return
		const content = window.getContentBounds()
		const cursor = cursorPoint()
		const x = cursor.x - content.x
		const y = cursor.y - content.y
		this.#reveal.update({ x, y, inWindow: x >= 0 && y >= 0 && x < content.width && y < content.height, now: this.#clock() })
	}

	#focusSurfaceIfCovering(): void {
		if (!this.gated && !this.#settingsOpen) return
		void this.#ensure().then((view) => {
			if (isViewLive(this.#o.window, view)) view.webContents.focus()
		})
	}

	#sync(): void {
		const content = this.#o.window.getContentBounds()
		const panels = this.#panels
		this.#region = surfaceRegion({
			content: { width: content.width, height: content.height },
			page: this.pageRect(),
			gated: this.gated,
			settingsOpen: this.#settingsOpen,
			sidebarRevealed: this.#reveal.revealed,
			panelFrames: panels.visible.map((p) => p.frame),
			dragging: panels.dragging,
		})
		const region = this.#region
		if (!region) {
			this.#view?.setVisible(false)
			return
		}
		void this.#ensure().then((view) => {
			if (!isViewLive(this.#o.window, view)) return
			// The region may have changed while the view was loading.
			const now = this.#region
			if (!now) return
			view.setBounds(now)
			this.raise()
			view.setVisible(true)
			this.#sendState()
		})
	}

	#ensure(): Promise<WebContentsView> {
		this.#ready ??= this.#create()
		return this.#ready
	}

	async #create(): Promise<WebContentsView> {
		const view = new WebContentsView({
			webPreferences: {
				preload: this.#o.preload,
				sandbox: true,
				contextIsolation: true,
				nodeIntegration: false,
				partition: 'zing-chrome',
			},
		})
		view.setBackgroundColor('#00000000')
		view.setVisible(false)
		this.#o.window.contentView.addChildView(view)
		this.#o.onContentsCreated(view.webContents)
		this.#view = view
		await loadUnlessGone(view.webContents, this.#o.load)
		this.#loaded = true
		return view
	}

	#send<C extends EventChannel>(channel: C, payload: EventContract[C]): void {
		const { window } = this.#o
		if (!window.isDestroyed()) window.webContents.send(channel, payload)
		if (this.#view && this.#loaded && !this.#view.webContents.isDestroyed()) this.#view.webContents.send(channel, payload)
	}

	#sendState(): void {
		this.#send('shell:state', this.state())
	}

	#sendPanels(): void {
		this.#send('toolpanel:state', this.#panels.state())
	}
}
