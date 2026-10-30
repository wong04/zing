import { WebContentsView, type BrowserWindowConstructorOptions, type Session, type WebContents } from 'electron'
import { stripTrackers } from '@shared/browser/linkCleaner'
import { NEW_TAB_TITLE, NEW_TAB_URL, TabStore, type TabId, type TabRecord } from '@shared/model'
import type { TabView, TabsState } from '@shared/ipc'

/** Where a tab's view is put on screen. The window owns layout and stacking. */
export interface PageHost {
	show(view: WebContentsView): void
	hide(view: WebContentsView): void
}

export interface TabServiceOptions {
	/** Passed in, never a module global, so Spaces/profiles can each bring their own later. */
	readonly session: Session
	readonly host: PageHost
	readonly onChange: () => void
	/** Every page WebContents the service creates or adopts, for shortcut routing. */
	readonly onContentsCreated: (contents: WebContents) => void
	readonly onLoadFailed: (tab: TabRecord, description: string) => void
	readonly now?: () => number
}

const ERR_ABORTED = -3

export class PopupAdoptionError extends Error {
	constructor(readonly url: string) {
		super(`Chromium offered no WebContents to adopt for the popup ${url}`)
		this.name = 'PopupAdoptionError'
	}
}

/**
 * Owns a window's tab records and attaches a WebContentsView to a record only
 * when it is first activated. Every tab operation goes through here, which is
 * the seam a `chrome.tabs` shim and sleeping tabs (dropping a view while
 * keeping its record) will wrap later.
 */
export class TabService {
	readonly #store: TabStore
	readonly #views = new Map<TabId, WebContentsView>()
	readonly #openers = new Map<TabId, TabId>()
	readonly #options: TabServiceOptions

	constructor(options: TabServiceOptions) {
		this.#options = options
		this.#store = new TabStore(options.now)
	}

	get activeId(): TabId | undefined {
		return this.#store.activeId
	}

	get canReopen(): boolean {
		return this.#store.canReopen
	}

	state(): TabsState {
		return { tabs: this.#store.tabs.map((t) => this.#view(t)), activeId: this.#store.activeId }
	}

	/** A record only; no renderer until it is activated. */
	create(url: string): TabRecord {
		const record = this.#store.add(url)
		this.#changed()
		return record
	}

	open(url: string): TabRecord {
		const record = this.create(url)
		this.activate(record.id)
		return record
	}

	activate(id: TabId): void {
		const previous = this.#store.activeId
		const record = this.#store.activate(id)
		if (previous !== undefined && previous !== id) this.#hideView(previous)
		this.#options.host.show(this.#ensureView(record))
		this.#changed()
	}

	/** Closing the last tab leaves a fresh one, as in Krackn: a window is never tabless. */
	close(id: TabId): void {
		const { nextActiveId } = this.#store.close(id)
		this.#destroyView(id)
		const opener = this.#openers.get(id)
		this.#openers.delete(id)
		if (this.#store.tabs.length === 0) {
			this.open(NEW_TAB_URL)
			return
		}
		if (opener !== undefined && nextActiveId !== undefined && this.#store.has(opener)) {
			this.activate(opener)
			return
		}
		if (nextActiveId !== undefined) this.activate(nextActiveId)
		this.#changed()
	}

	navigate(id: TabId, rawUrl: string): void {
		const url = stripTrackers(rawUrl)
		this.#store.update(id, { url })
		const view = this.#views.get(id)
		if (view) this.#load(view.webContents, url)
		this.#changed()
	}

	move(id: TabId, index: number): void {
		this.#store.move(id, index)
		this.#changed()
	}

	pin(id: TabId): void {
		this.#store.pin(id)
		this.#changed()
	}

	unpin(id: TabId): void {
		this.#store.unpin(id)
		this.#changed()
	}

	get(id: TabId): TabRecord {
		return this.#store.get(id)
	}

	reopenClosed(): TabRecord | undefined {
		const record = this.#store.reopenClosed()
		if (!record) return undefined
		this.activate(record.id)
		return record
	}

	nextId(): TabId | undefined {
		return this.#store.nextId()
	}

	previousId(): TabId | undefined {
		return this.#store.previousId()
	}

	idForShortcut(n: number): TabId | undefined {
		return this.#store.idForShortcut(n)
	}

	/** The active tab's live WebContents. Always present: activation attaches one. */
	activeContents(): WebContents | undefined {
		const id = this.#store.activeId
		return id === undefined ? undefined : this.#views.get(id)?.webContents
	}

	focusPage(id: TabId): void {
		this.#views.get(id)?.webContents.focus()
	}

	/** Window teardown: every page renderer goes, records go with the window. */
	destroy(): void {
		for (const id of [...this.#views.keys()]) this.#destroyView(id)
	}

	#view(record: TabRecord): TabView {
		const contents = this.#views.get(record.id)?.webContents
		if (!contents) return { ...record, loading: false, canGoBack: false, canGoForward: false }
		return {
			...record,
			loading: contents.isLoading(),
			canGoBack: contents.navigationHistory.canGoBack(),
			canGoForward: contents.navigationHistory.canGoForward(),
		}
	}

	#ensureView(record: TabRecord): WebContentsView {
		const existing = this.#views.get(record.id)
		if (existing) return existing
		const view = new WebContentsView({
			webPreferences: {
				session: this.#options.session,
				sandbox: true,
				contextIsolation: true,
				nodeIntegration: false,
			},
		})
		this.#attach(record.id, view)
		this.#load(view.webContents, record.url)
		return view
	}

	#attach(id: TabId, view: WebContentsView): void {
		this.#views.set(id, view)
		this.#wire(id, view)
		this.#options.onContentsCreated(view.webContents)
	}

	#load(contents: WebContents, url: string): void {
		// Failures are reported once, from did-fail-load, which also covers
		// navigations the page starts itself; the promise would report them twice.
		contents.loadURL(url).catch(() => undefined)
	}

	#hideView(id: TabId): void {
		const view = this.#views.get(id)
		if (view) this.#options.host.hide(view)
	}

	#destroyView(id: TabId): void {
		const view = this.#views.get(id)
		if (!view) return
		this.#views.delete(id)
		this.#options.host.hide(view)
		if (!view.webContents.isDestroyed()) view.webContents.close()
	}

	#wire(id: TabId, view: WebContentsView): void {
		const contents = view.webContents
		const update = (patch: Parameters<TabStore['update']>[1]): void => {
			if (!this.#store.has(id)) return
			this.#store.update(id, patch)
			this.#changed()
		}
		// Links a page offers are cleaned at navigation time, so redirects and pasted URLs are covered too.
		contents.on('will-navigate', (event, url) => {
			const clean = stripTrackers(url)
			if (clean === url) return
			event.preventDefault()
			this.#load(contents, clean)
		})
		contents.on('did-start-loading', () => this.#changed())
		contents.on('did-stop-loading', () => this.#changed())
		contents.on('did-navigate', (_e, url) => update({ url, faviconUrl: undefined }))
		contents.on('did-navigate-in-page', (_e, url, isMainFrame) => {
			if (isMainFrame) update({ url })
		})
		contents.on('page-title-updated', (_e, title) => update({ title: title || NEW_TAB_TITLE }))
		contents.on('page-favicon-updated', (_e, favicons) => update({ faviconUrl: favicons[0] }))
		contents.on('did-fail-load', (_e, code, description, _url, isMainFrame) => {
			if (!isMainFrame || code === ERR_ABORTED || !this.#store.has(id)) return
			this.#options.onLoadFailed(this.#store.get(id), description)
		})
		// window.close() from a script-opened popup destroys its WebContents.
		contents.on('destroyed', () => {
			if (this.#views.get(id) !== view) return
			this.#views.delete(id)
			this.#options.host.hide(view)
			if (this.#store.has(id)) this.close(id)
		})
		contents.setWindowOpenHandler((details) => ({
			action: 'allow',
			// Tabs a page opens shouldn't die with it; only real popups return to their opener.
			outlivesOpener: true,
			createWindow: (options) => this.#adoptPopup(id, details.url, details.disposition, options),
		}))
	}

	/**
	 * A popup must be the WebContents Chromium created for it, not a fresh tab
	 * loading the same URL, or `window.opener` is null and OAuth popups (Google,
	 * Microsoft, payment pages) have nowhere to send their result.
	 */
	#adoptPopup(
		openerId: TabId,
		url: string,
		disposition: string,
		options: BrowserWindowConstructorOptions,
	): WebContents {
		const guest = (options as { webContents?: WebContents }).webContents
		if (!guest) throw new PopupAdoptionError(url)
		const view = new WebContentsView({ webContents: guest })
		const record = this.#store.addAfter(openerId, url)
		this.#attach(record.id, view)
		this.#openers.set(record.id, openerId)
		if (disposition === 'background-tab') {
			this.#changed()
			return view.webContents
		}
		this.activate(record.id)
		return view.webContents
	}

	#changed(): void {
		this.#options.onChange()
	}
}
