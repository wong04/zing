import { Menu, type BrowserWindow, type MenuItemConstructorOptions } from 'electron'
import { isFolder, type BookmarkNode } from '@shared/library/library'
import { LIBRARY_PANEL_WIDTH, type LibraryAction, type LibraryOpenTarget, type LibraryPanel, type LibraryViewState, type MenuAnchor } from '@shared/ipc'
import type { TabId, TabRecord } from '@shared/model'
import type { SettingsStore } from '@shared/settings'
import type { LibraryStore } from './library-store'

export interface LibraryTabs {
	readonly activeId: TabId | undefined
	get(id: TabId): TabRecord
	open(url: string): TabRecord
	navigate(id: TabId, url: string): void
	focusPage(id: TabId): void
}

export interface LibraryControllerOptions {
	readonly store: LibraryStore
	readonly settings: SettingsStore
	readonly tabs: LibraryTabs
	readonly window: BrowserWindow
	/** The Favourites bar or the side panel changed size, so the page must move. */
	readonly onLayout: () => void
	/** The width of whatever is docked to the left of the panel. */
	readonly offset: () => number
	readonly notify: (text: string) => void
}

type Status = 'done' | 'not-applicable'

const SAVABLE = /^https?:/i

function titleOf(tab: TabRecord): string {
	return tab.title.trim() || new URL(tab.url).hostname
}

/** One window's view of the shared library: which panel is docked, whether the bar shows, and what opens where. */
export class LibraryController {
	#panel: LibraryPanel | null = null
	#pushedOffset = 0
	readonly #unsubscribe: readonly (() => void)[]

	constructor(private readonly options: LibraryControllerOptions) {
		this.#unsubscribe = [
			options.store.subscribe(() => this.push()),
			options.settings.subscribe((_settings, key) => {
				if (key !== 'showFavouritesBar') return
				options.onLayout()
				this.push()
			}),
		]
	}

	get favouritesBar(): boolean {
		return this.options.settings.get('showFavouritesBar')
	}

	/** How far the docked panel pushes the page from the left edge. */
	get leftInset(): number {
		return this.#panel ? LIBRARY_PANEL_WIDTH : 0
	}

	state(): LibraryViewState {
		return { library: this.options.store.snapshot(), panel: this.#panel, favouritesBar: this.favouritesBar, offset: this.options.offset() }
	}

	/** The sidebar may have been pinned or unpinned; the panel moves with it. */
	layoutChanged(): void {
		if (this.#panel && this.options.offset() !== this.#pushedOffset) this.push()
	}

	push(): void {
		this.#pushedOffset = this.options.offset()
		const chrome = this.options.window.webContents
		if (!chrome.isDestroyed()) chrome.send('library:state', this.state())
	}

	addFavourite(): Status {
		const tab = this.#savableActiveTab()
		if (!tab) return 'not-applicable'
		const outcome = this.options.store.addFavourite(titleOf(tab), tab.url)
		this.options.notify(outcome === 'added' ? 'Added to Favourites.' : 'Already in Favourites.')
		return 'done'
	}

	addReadingList(): Status {
		const tab = this.#savableActiveTab()
		if (!tab) return 'not-applicable'
		const outcome = this.options.store.addReading(titleOf(tab), tab.url)
		this.options.notify(outcome === 'added' ? 'Added to Reading List.' : 'Already in Reading List.')
		return 'done'
	}

	toggleFavouritesBar(): Status {
		this.options.settings.set('showFavouritesBar', !this.favouritesBar)
		return 'done'
	}

	togglePanel(panel: LibraryPanel): Status {
		this.setPanel(this.#panel === panel ? null : panel)
		return 'done'
	}

	setPanel(panel: LibraryPanel | null): void {
		if (this.#panel === panel) return
		this.#panel = panel
		this.options.onLayout()
		this.push()
		// The page view held focus; the panel's list is in the chrome renderer.
		if (panel) this.options.window.webContents.focus()
	}

	/** The id of a folder the action created, so the panel can start renaming it. */
	apply(action: LibraryAction): string | undefined {
		return this.options.store.apply(action)
	}

	open(id: string, target: LibraryOpenTarget): void {
		const url = this.options.store.urlOf(id)
		if (url === undefined) return
		const active = this.options.tabs.activeId
		if (target === 'tab' || active === undefined) {
			this.options.tabs.open(url)
			return
		}
		this.options.tabs.navigate(active, url)
		this.options.tabs.focusPage(active)
	}

	/** Every link under a folder, each in its own tab. */
	openAll(id: string): void {
		for (const url of this.options.store.urlsOf(id)) this.options.tabs.open(url)
	}

	popupFolderMenu(id: string, anchor: MenuAnchor): void {
		const children = this.options.store.childrenOf(id)
		if (children.length === 0) return
		const items: MenuItemConstructorOptions[] = [
			...children.map((node) => this.#menuItem(node)),
			{ type: 'separator' },
			{ label: 'Open All in New Tabs', click: () => this.openAll(id) },
		]
		this.#popup(items, anchor)
	}

	popupItemMenu(id: string, anchor: MenuAnchor): void {
		const items: MenuItemConstructorOptions[] = this.options.store.isFolderNode(id)
			? [{ label: 'Open All in New Tabs', click: () => this.openAll(id) }]
			: [
					{ label: 'Open', click: () => this.open(id, 'current') },
					{ label: 'Open in New Tab', click: () => this.open(id, 'tab') },
				]
		items.push({ type: 'separator' }, { label: 'Remove', click: () => this.options.store.apply({ type: 'remove', ids: [id] }) })
		this.#popup(items, anchor)
	}

	dispose(): void {
		for (const unsubscribe of this.#unsubscribe) unsubscribe()
	}

	#menuItem(node: BookmarkNode): MenuItemConstructorOptions {
		if (isFolder(node)) {
			const submenu = node.children.map((child) => this.#menuItem(child))
			return { label: node.title, submenu: submenu.length > 0 ? submenu : [{ label: '(Empty)', enabled: false }] }
		}
		return { label: node.title, click: () => this.open(node.id, 'current') }
	}

	#popup(template: MenuItemConstructorOptions[], anchor: MenuAnchor): void {
		Menu.buildFromTemplate(template).popup({ window: this.options.window, x: Math.round(anchor.x), y: Math.round(anchor.y) })
	}

	#savableActiveTab(): TabRecord | undefined {
		const id = this.options.tabs.activeId
		if (id === undefined) return undefined
		const tab = this.options.tabs.get(id)
		return SAVABLE.test(tab.url) ? tab : undefined
	}
}
