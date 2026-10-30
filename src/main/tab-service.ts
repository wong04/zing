import { WebContentsView, type BrowserWindowConstructorOptions, type Session, type WebContents } from 'electron'
import { stripTrackers } from '@shared/browser/linkCleaner'
import { startPageURL } from '@shared/browser/startPage'
import { isStartPage } from './start-page'
import { NEW_TAB_TITLE, TabStore, type TabId, type TabRecord } from '@shared/model'
import type { TabList, TabView } from '@shared/ipc'
import type { SiteDataKeeper } from './privacy'

import type { SessionTab } from '@shared/session/session'

/** Where a tab's view is put on screen. The window owns layout and stacking. */
export interface PageHost {
	show(view: WebContentsView): void
	hide(view: WebContentsView): void
}

export interface TabServiceOptions {
	/** Passed in, never a module global, so Spaces/profiles can each bring their own later. */
	readonly session: Session
	readonly host: PageHost
	/** Purges a site's data when the last tab that touched it closes. */
	readonly siteData?: SiteDataKeeper
	readonly onChange: () => void
	/** Every page WebContents the service creates or adopts, for shortcut routing. */
	readonly onContentsCreated: (contents: WebContents) => void
	readonly onLoadFailed: (tab: TabRecord, description: string) => void
	readonly now?: () => number
	/** A sleeping tab just got a fresh page, so saved scroll can be put back. */
	readonly onWake?: (id: TabId, contents: WebContents) => void
	/** The memory-only favicon for a record, if one has loaded. */
	readonly iconFor?: (record: TabRecord) => string | undefined
}

const ERR_ABORTED = -3

export class ActiveTabSleepError extends Error {
	constructor(readonly tabId: TabId) {
		super(`Tab ${tabId} is the active tab and cannot sleep`)
		this.name = 'ActiveTabSleepError'
	}
}

export class TabOrderError extends Error {
	constructor(detail: string) {
		super(`Can't apply that tab order: ${detail}`)
		this.name = 'TabOrderError'
	}
}

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
	readonly #sleeping = new Set<TabId>()
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

	get records(): readonly TabRecord[] {
		return this.#store.tabs
	}

	state(): TabList {
		return { tabs: this.#store.tabs.map((t) => this.#view(t)), activeId: this.#store.activeId }
	}

	hasView(id: TabId): boolean {
		return this.#views.has(id)
	}

	isSleeping(id: TabId): boolean {
		return this.#sleeping.has(id)
	}

	contentsFor(id: TabId): WebContents | undefined {
		return this.#views.get(id)?.webContents
	}

	idOf(contents: WebContents): TabId | undefined {
		for (const [id, view] of this.#views) if (view.webContents === contents) return id
		return undefined
	}

	liveContents(): WebContents[] {
		return [...this.#views.values()].map((v) => v.webContents)
	}

	/** The icon the page declared, as the favicon probe chose it. */
	setFaviconUrl(id: TabId, faviconUrl: string | undefined): void {
		if (!this.#store.has(id)) return
		this.#store.update(id, { faviconUrl })
		this.#changed()
	}

	/** Drops the renderer but keeps the record, so the tab wakes at its URL when next activated. */
	sleep(id: TabId): void {
		this.#store.get(id)
		if (id === this.#store.activeId) throw new ActiveTabSleepError(id)
		if (!this.#views.has(id)) return
		this.#destroyView(id)
		this.#sleeping.add(id)
		this.#changed()
	}

	/** Brings back saved tabs as records; only the one that was in front gets a renderer. */
	restore(tabs: readonly SessionTab[]): void {
		const records = tabs.map((t) => this.#store.addRestored({ ...t, title: t.title || NEW_TAB_TITLE }))
		const front = records[Math.max(0, tabs.findIndex((t) => t.active))]
		if (front) this.activate(front.id)
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
		if (previous !== undefined && previous !== id) {
			this.#hideView(previous)
			if (this.#store.has(previous)) this.#store.touch(previous)
		}
		this.#options.host.show(this.#ensureView(record))
		this.#changed()
	}

	/** Closing the last tab leaves a fresh one, as in Krackn: a window is never tabless. */
	close(id: TabId): void {
		const { nextActiveId } = this.#store.close(id)
		this.#destroyView(id)
		this.#options.siteData?.release(id)
		this.#sleeping.delete(id)
		const opener = this.#openers.get(id)
		this.#openers.delete(id)
		if (this.#store.tabs.length === 0) {
			this.open(startPageURL)
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

	/** Puts the tabs in exactly this order (pinned first); the arrangement and drag code decide what it is. */
	applyOrder(ids: readonly TabId[]): void {
		const current = this.#store.tabs.map((t) => t.id)
		if (ids.length !== current.length) throw new TabOrderError(`${ids.length} ids for ${current.length} tabs`)
		if (ids.every((id, i) => id === current[i])) return
		ids.forEach((id, i) => this.#store.move(id, i))
		if (!this.#store.tabs.every((t, i) => t.id === ids[i])) throw new TabOrderError('it puts a tab across the pinned divider')
		this.#changed()
	}

	/** Reloads a tab that has a page; a tab that was never activated has nothing to reload. */
	reload(id: TabId): void {
		this.#views.get(id)?.webContents.reload()
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
		for (const { id } of this.#store.tabs) this.#options.siteData?.release(id)
	}

	#view(record: TabRecord): TabView {
		const contents = this.#views.get(record.id)?.webContents
		const sleeping = this.#sleeping.has(record.id)
		const icon = this.#options.iconFor?.(record)
		if (!contents) return { ...record, sleeping, icon, loading: false, canGoBack: false, canGoForward: false }
		return {
			...record,
			sleeping,
			icon,
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
		if (this.#sleeping.delete(record.id)) this.#options.onWake?.(record.id, view.webContents)
		return view
	}

	#attach(id: TabId, view: WebContentsView): void {
		this.#views.set(id, view)
		this.#options.siteData?.track(id, view.webContents)
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
		// will-navigate is not emitted for server redirects, which can add tracking parameters too.
		contents.on('will-redirect', (event, url) => {
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
		contents.on('page-title-updated', (_e, title) => update({ title: !title || isStartPage(contents.getURL()) ? NEW_TAB_TITLE : title }))
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
