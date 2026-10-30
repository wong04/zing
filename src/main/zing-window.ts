import { app, BrowserWindow, Menu, type Rectangle, type Session, type WebContents, type WebContentsView } from 'electron'
import type { CommandId } from '@shared/commands'
import { CHROME_LAYOUT, type CommandOutcome, type MenuAnchor, type TabsState, type TitleBarColors } from '@shared/ipc'
import { stripTrackers } from '@shared/browser/linkCleaner'
import { startPageURL } from '@shared/browser/startPage'
import { NEW_TAB_URL, type TabId } from '@shared/model'
import type { SettingsStore } from '@shared/settings'
import { resolveAddress } from './address'
import type { AccountStore } from './account'
import { runCommand } from './command-handlers'
import { FaviconService } from './favicon-service'
import { FrameBatcher } from './frame-batcher'
import { routeShortcuts } from './keyboard'
import { LibraryController } from './library-controller'
import type { LibraryStore } from './library-store'
import { OverlayLayer } from './overlay'
import { PageFeatures } from './page-actions'
import type { SiteDataKeeper } from './privacy'
import { ShellController } from './shell'
import { PRELOAD_PATH, loadChrome, loadOverlay, loadSurface, loadPageUi, loadStackDropdown, loadTabOverview } from './renderer-page'
import { StackDropdown, stackMembersOf } from './stack-dropdown'
import { isStartPage, StartPage } from './start-page'
import { TabOverview } from './tab-overview'
import { closeOtherTabs, stackMenuTemplate, stripMenuTemplate, tabMenuTemplate } from './tab-menus'
import { TabOrdering } from './tab-ordering'
import { TabService } from './tab-service'
import { TabLifecycle, appClock } from './tab-lifecycle'
import { takePendingRestore } from './session-restore'
import { chromeInsets, VERTICAL_TABS, type ChromeInsets } from '@shared/layout/chrome'
import { SIDEBAR_COLUMN_WIDTH } from '@shared/shell/shellState'

export interface ZingWindowOptions {
	readonly session: Session
	readonly settings: SettingsStore
	readonly appMenu: () => Menu
	readonly onClosed: (window: ZingWindow) => void
	readonly siteData: SiteDataKeeper | undefined
	/** Shared by every window, so a bookmark added in one shows in all. */
	readonly library: LibraryStore
	readonly account: AccountStore
	readonly newWindow: () => void
	/** Only the primary window saves the session, so windows never overwrite each other. */
	readonly isPrimary: (window: ZingWindow) => boolean
}

export class UnsupportedUrlError extends Error {
	constructor(readonly url: string) {
		super(`Zing can't open ${url}`)
		this.name = 'UnsupportedUrlError'
	}
}

const NAVIGABLE_PROTOCOLS = new Set(['http:', 'https:', 'about:', 'file:'])

function assertNavigable(url: string): void {
	if (url === startPageURL) return
	if (!URL.canParse(url) || !NAVIGABLE_PROTOCOLS.has(new URL(url).protocol)) throw new UnsupportedUrlError(url)
}

/** Fullscreen video: the page view takes the whole window, chrome included. */
function fullBounds(content: Rectangle): Rectangle {
	return { x: 0, y: 0, width: content.width, height: content.height }
}

/** Page bounds in whole DIPs; every inset is a multiple of 4, so the edges land on device pixels too. */
function pageBounds(content: Rectangle, { top, left, right }: ChromeInsets): Rectangle {
	return { x: left, y: top, width: Math.max(0, content.width - left - right), height: Math.max(0, content.height - top) }
}

/**
 * One browser window: the chrome renderer at the bottom, the active tab's
 * page view above it, and the overlay on top.
 */
export class ZingWindow {
	readonly window: BrowserWindow
	readonly tabs: TabService
	readonly ordering: TabOrdering
	readonly stacks: StackDropdown
	readonly library: LibraryController
	readonly overview: TabOverview
	readonly #startPage: StartPage
	readonly #favicons: FaviconService
	readonly #overlay: OverlayLayer
	readonly features: PageFeatures
	readonly shell: ShellController
	readonly account: AccountStore
	readonly #batcher: FrameBatcher
	readonly #options: ZingWindowOptions
	readonly #trusted = new Set<WebContents>()
	readonly #tabsListeners = new Set<() => void>()
	#lifecycle: TabLifecycle | undefined
	#shown: WebContentsView | undefined
	#railWidth: number = VERTICAL_TABS.defaultWidth

	constructor(options: ZingWindowOptions) {
		this.#options = options
		this.account = options.account
		this.window = new BrowserWindow({
			width: 1280,
			height: 820,
			minWidth: 480,
			minHeight: 320,
			show: false,
			title: 'Zing',
			titleBarStyle: 'hidden',
			// Transparent so the tab strip and Mica show behind the native caption
			// buttons; the symbol colour comes from the renderer's theme tokens.
			titleBarOverlay: { color: '#00000000', height: CHROME_LAYOUT.tabStripHeight },
			backgroundMaterial: 'mica',
			backgroundColor: '#00000000',
			webPreferences: {
				preload: PRELOAD_PATH,
				contextIsolation: true,
				sandbox: true,
				nodeIntegration: false,
				// The chrome keeps nothing either: no disk cache, no storage.
				partition: 'zing-chrome',
				spellcheck: false,
			},
		})
		this.#trusted.add(this.window.webContents)
		this.#batcher = new FrameBatcher(() => this.#sendState())
		this.#overlay = new OverlayLayer({
			window: this.window,
			preload: PRELOAD_PATH,
			load: loadOverlay,
			onContentsCreated: (c) => this.#adoptTrusted(c),
		})
		this.features = new PageFeatures({
			window: this.window,
			activeContents: () => this.tabs.activeContents(),
			notify: (text) => this.notify(text),
			relayout: () => this.#layout(),
			panels: {
				window: this.window,
				preload: PRELOAD_PATH,
				load: loadPageUi,
				onContentsCreated: (c) => this.#adoptTrusted(c),
				top: () => this.chromeInsets().top,
			},
		})
		this.tabs = new TabService({
			now: () => appClock().now(),
			onWake: (id, contents) => this.#lifecycle?.woke(id, contents),
			session: options.session,
			siteData: options.siteData,
			host: { show: (v) => this.#showPage(v), hide: (v) => this.#hidePage(v) },
			onChange: () => {
				this.#batcher.schedule()
				this.ordering.scheduleReorganize()
				this.features.activeChanged()
				this.overview.refresh()
				this.#lifecycle?.reschedule()
				for (const listener of this.#tabsListeners) listener()
			},
			iconFor: (record) => this.#favicons.iconFor(record),
			onContentsCreated: (c) => {
				routeShortcuts(c, (id) => this.run(id))
				this.features.attach(c)
				this.#favicons.attach(c, this.tabs)
				this.#startPage.attach(c, {
					onSearch: (contents, query) => {
						const id = this.tabs.idOf(contents)
						if (id !== undefined) this.navigate(id, query)
					},
					onEditShortcuts: () => void this.run('app.settings'),
				})
			},
			onLoadFailed: (tab, description) => this.notify(`Couldn't load ${tab.url} (${description})`),
		})
		this.#startPage = new StartPage(options.settings)
		this.#favicons = new FaviconService(options.session, () => this.#batcher.schedule())
		this.ordering = new TabOrdering(this.tabs, options.settings, { notify: (text) => this.notify(text) })
		this.stacks = new StackDropdown({
			window: this.window,
			preload: PRELOAD_PATH,
			load: loadStackDropdown,
			onContentsCreated: (c) => this.#adoptTrusted(c),
			members: (key) => stackMembersOf(this.tabs.state(), this.ordering.stacks(), key),
			onChange: () => this.#batcher.schedule(),
		})
		this.overview = new TabOverview({
			window: this.window,
			preload: PRELOAD_PATH,
			load: loadTabOverview,
			onContentsCreated: (c) => this.#adoptTrusted(c),
			tabs: this.tabs,
			top: () => this.chromeInsets().top,
		})
		this.library = new LibraryController({
			store: options.library,
			settings: options.settings,
			tabs: this.tabs,
			window: this.window,
			onLayout: () => this.#layout(),
			offset: () => this.shell.pinnedWidth,
			notify: (text) => this.notify(text),
		})
		this.window.once('closed', this.#startPage.follow(() => this.tabs.liveContents()))
		const unsubscribeSettings = options.settings.subscribe((_settings, key) => {
			if (key === 'tabDropdowns') this.stacks.close()
			if (key !== 'tabOrganization' && key !== 'tabDropdowns') return
			this.ordering.reorganize()
			this.#batcher.schedule()
		})
		this.window.once('closed', unsubscribeSettings)
		this.shell = new ShellController({
			window: this.window,
			settings: options.settings,
			account: options.account,
			session: options.session,
			preload: PRELOAD_PATH,
			load: loadSurface,
			onContentsCreated: (c) => this.#adoptTrusted(c),
			appVersion: app.getVersion(),
			focusPage: () => {
				const active = this.tabs.activeId
				if (active !== undefined) this.tabs.focusPage(active)
			},
			notify: (text) => this.notify(text),
			relayout: () => this.#layout(),
			pageArea: () => this.#pageRect(),
		})
		routeShortcuts(this.window.webContents, (id) => this.run(id))
		this.#wireWindow()
		this.#lifecycle = new TabLifecycle({ tabs: this.tabs, settings: options.settings, clock: appClock() })
		const restored = takePendingRestore()
		if (restored.length > 0) this.tabs.restore(restored)
		else this.tabs.open(this.#homepageUrl())
		void loadChrome(this.window.webContents)
	}

	/** True for the oldest open window; the session-restore code saves only from this one. */
	get isPrimary(): boolean {
		return this.#options.isPrimary(this)
	}

	/** Fires after any change to this window's tabs, e.g. so the primary window's session can be saved. */
	onTabsChanged(listener: () => void): void {
		this.#tabsListeners.add(listener)
	}

	isTrusted(contents: WebContents): boolean {
		return this.#trusted.has(contents)
	}

	run(id: CommandId): CommandOutcome {
		// Behind the account gate nothing may act on the browser.
		if (this.shell.gated) return { status: 'not-applicable' }
		const outcome = runCommand(id, {
			tabs: this.tabs,
			shell: this.shell,
			features: this.features,
			window: this.window,
			library: this.library,
			overview: this.overview,
			homepage: () => this.#homepageUrl(),
			focusAddress: () => this.#focusAddress(),
			newWindow: () => this.#options.newWindow(),
		})
		if (outcome.status === 'not-implemented') this.notify(`${outcome.label} isn't implemented yet`)
		return outcome
	}

	/** A link from another app: a new tab, or the lone blank tab reused. Non-web links are refused. */
	openLink(url: string): void {
		assertNavigable(url)
		const { tabs } = this.tabs.state()
		const [only] = tabs
		if (tabs.length === 1 && (only?.url === NEW_TAB_URL || (only !== undefined && isStartPage(only.url)))) this.navigate(only.id, url)
		else this.tabs.open(stripTrackers(url))
	}

	reveal(): void {
		if (this.window.isMinimized()) this.window.restore()
		this.window.show()
		this.window.focus()
	}

	/** Takes whatever was typed or a ready URL; blank input does nothing. */
	navigate(id: TabId, input: string): void {
		const url = resolveAddress(input, this.#options.settings.get('searchEngine'))
		if (url === undefined) return
		try {
			assertNavigable(url)
		} catch (error) {
			if (!(error instanceof UnsupportedUrlError)) throw error
			this.notify(error.message)
			return
		}
		this.tabs.navigate(id, url)
		this.tabs.focusPage(id)
	}

	notify(text: string): void {
		void this.#overlay.showNotice(text)
	}

	setTitleBarColors(colors: TitleBarColors): void {
		this.window.setTitleBarOverlay({ color: '#00000000', symbolColor: colors.symbolColor })
	}

	popupAppMenu(anchor: MenuAnchor): void {
		this.#options.appMenu().popup({ window: this.window, x: Math.round(anchor.x), y: Math.round(anchor.y) })
	}

	/** The tab list plus how to draw it; the chrome renderer's whole picture of the strip. */
	state(): TabsState {
		return {
			...this.tabs.state(),
			organization: this.ordering.organization,
			dropdowns: this.ordering.dropdowns,
			openStack: this.stacks.openKey,
		}
	}

	closeOtherTabs(keep: TabId): void {
		closeOtherTabs(this.tabs, keep)
	}

	popupTabMenu(id: TabId, anchor: MenuAnchor): void {
		this.#popup(tabMenuTemplate(this, id), anchor)
	}

	popupStackMenu(key: string, anchor: MenuAnchor): void {
		this.#popup(stackMenuTemplate(this, key), anchor)
	}

	popupStripMenu(anchor: MenuAnchor): void {
		this.#popup(stripMenuTemplate(this), anchor)
	}

	#popup(template: Parameters<typeof Menu.buildFromTemplate>[0], anchor: MenuAnchor): void {
		if (template.length === 0) return
		Menu.buildFromTemplate(template).popup({ window: this.window, x: Math.round(anchor.x), y: Math.round(anchor.y) })
	}

	#adoptTrusted(contents: WebContents): void {
		this.#trusted.add(contents)
		routeShortcuts(contents, (id) => this.run(id))
	}

	#wireWindow(): void {
		this.window.once('ready-to-show', () => this.window.show())
		this.window.on('resize', () => this.#layout())
		this.window.on('closed', () => {
			this.#batcher.cancel()
			this.#lifecycle?.dispose()
			this.library.dispose()
			this.overview.destroy()
			this.ordering.cancel()
			this.stacks.destroy()
			this.shell.destroy()
			this.tabs.destroy()
			this.#overlay.destroy()
			this.features.destroy()
			this.#options.onClosed(this)
		})
	}

	#showPage(view: WebContentsView): void {
		if (this.#shown && this.#shown !== view) this.#hidePage(this.#shown)
		this.#shown = view
		view.setBounds(this.#pageRect())
		this.window.contentView.addChildView(view)
		this.shell.raise()
		this.#overlay.raise()
		this.features.raiseLayers()
		this.stacks.raise()
		this.overview.raise()
	}

	#hidePage(view: WebContentsView): void {
		// When a page closes itself (window.close() in a popup), Electron has
		// already detached `view.webContents` by the time 'destroyed' reaches us.
		const contents = view.webContents as WebContents | undefined
		if (this.#shown === view && contents && !contents.isDestroyed()) this.overview.rememberLeaving(contents)
		if (this.#shown === view) this.#shown = undefined
		if (this.window.isDestroyed() || !this.window.contentView.children.includes(view)) return
		this.window.contentView.removeChildView(view)
	}

	#pageRect(): Rectangle {
		const content = this.window.getContentBounds()
		if (this.features.fullscreen.active) return fullBounds(content)
		return pageBounds(content, this.chromeInsets())
	}

	/**
	 * Where the chrome ends and the page begins on each side. Built from settings
	 * and the library, not from the shell, so layers created before the shell can
	 * ask for it. With tabs on the left the app sidebar folds into the tab rail,
	 * so it only takes its own column in the other positions.
	 */
	chromeInsets(): ChromeInsets {
		const settings = this.#options.settings
		const position = settings.get('tabBarPosition')
		const sidebar = position !== 'left' && settings.get('sidebarPinned') ? SIDEBAR_COLUMN_WIDTH : 0
		return chromeInsets({
			position,
			favouritesBar: this.library.favouritesBar,
			railWidth: this.#railWidth,
			leftDock: sidebar + this.library.leftInset,
		})
	}

	/** The vertical tab rail was resized or collapsed in the chrome renderer. */
	setRailWidth(width: number): void {
		this.#railWidth = width
		this.#layout()
	}

	#layout(): void {
		this.#shown?.setBounds(this.#pageRect())
		this.shell.layout()
		this.features.layout()
		this.#overlay.layout()
		this.stacks.layout()
		this.overview.layout()
		this.library.layoutChanged()
	}

	/** The configured homepage by the address bar's own rule; blank means the start page. */
	#homepageUrl(): string {
		const typed = this.#options.settings.get('homepage').trim()
		return resolveAddress(typed, this.#options.settings.get('searchEngine')) ?? startPageURL
	}

	#focusAddress(): void {
		this.window.webContents.focus()
		this.window.webContents.send('chrome:focus-address')
	}

	#sendState(): void {
		if (this.window.isDestroyed()) return
		this.window.webContents.send('tabs:state', this.state())
		this.stacks.refresh()
	}
}
