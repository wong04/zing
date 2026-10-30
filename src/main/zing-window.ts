import { BrowserWindow, Menu, type Rectangle, type Session, type WebContents, type WebContentsView } from 'electron'
import type { CommandId } from '@shared/commands'
import { CHROME_HEIGHT, CHROME_LAYOUT, type CommandOutcome, type MenuAnchor, type TitleBarColors } from '@shared/ipc'
import { NEW_TAB_URL, type TabId, type TabRecord } from '@shared/model'
import type { SettingsStore } from '@shared/settings'
import { resolveAddress } from './address'
import { runCommand } from './command-handlers'
import { FrameBatcher } from './frame-batcher'
import { routeShortcuts } from './keyboard'
import { OverlayLayer } from './overlay'
import { PRELOAD_PATH, loadChrome, loadOverlay } from './renderer-page'
import { TabService } from './tab-service'

export interface ZingWindowOptions {
	readonly session: Session
	readonly settings: SettingsStore
	readonly appMenu: () => Menu
	readonly onClosed: (window: ZingWindow) => void
}

export class UnsupportedUrlError extends Error {
	constructor(readonly url: string) {
		super(`Zing can't open ${url}`)
		this.name = 'UnsupportedUrlError'
	}
}

const NAVIGABLE_PROTOCOLS = new Set(['http:', 'https:', 'about:', 'file:'])

function assertNavigable(url: string): void {
	if (!URL.canParse(url) || !NAVIGABLE_PROTOCOLS.has(new URL(url).protocol)) throw new UnsupportedUrlError(url)
}

/** Page bounds in whole DIPs; CHROME_HEIGHT keeps the top edge on a device pixel too. */
function pageBounds(content: Rectangle): Rectangle {
	return { x: 0, y: CHROME_HEIGHT, width: content.width, height: Math.max(0, content.height - CHROME_HEIGHT) }
}

/**
 * One browser window: the chrome renderer at the bottom, the active tab's
 * page view above it, and the overlay on top.
 */
export class ZingWindow {
	readonly window: BrowserWindow
	readonly tabs: TabService
	readonly #overlay: OverlayLayer
	readonly #batcher: FrameBatcher
	readonly #options: ZingWindowOptions
	readonly #trusted = new Set<WebContents>()
	#shown: WebContentsView | undefined

	constructor(options: ZingWindowOptions) {
		this.#options = options
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
		this.tabs = new TabService({
			session: options.session,
			host: { show: (v) => this.#showPage(v), hide: (v) => this.#hidePage(v) },
			onChange: () => this.#batcher.schedule(),
			onContentsCreated: (c) => routeShortcuts(c, (id) => this.run(id)),
			onLoadFailed: (tab, description) => this.notify(`Couldn't load ${tab.url} (${description})`),
		})
		routeShortcuts(this.window.webContents, (id) => this.run(id))
		this.#wireWindow()
		this.tabs.open(NEW_TAB_URL)
		void loadChrome(this.window.webContents)
	}

	isTrusted(contents: WebContents): boolean {
		return this.#trusted.has(contents)
	}

	run(id: CommandId): CommandOutcome {
		const outcome = runCommand(id, {
			tabs: this.tabs,
			window: this.window,
			homepage: () => this.#options.settings.get('homepage').trim(),
			focusAddress: () => this.#focusAddress(),
		})
		if (outcome.status === 'not-implemented') this.notify(`${outcome.label} isn't implemented yet`)
		return outcome
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

	popupTabMenu(id: TabId, anchor: MenuAnchor): void {
		const record: TabRecord = this.tabs.get(id)
		const menu = Menu.buildFromTemplate([
			record.pinned
				? { label: 'Unpin Tab', click: () => this.tabs.unpin(id) }
				: { label: 'Pin Tab', click: () => this.tabs.pin(id) },
			{ type: 'separator' },
			{ label: 'Close Tab', click: () => this.tabs.close(id) },
		])
		menu.popup({ window: this.window, x: Math.round(anchor.x), y: Math.round(anchor.y) })
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
			this.tabs.destroy()
			this.#overlay.destroy()
			this.#options.onClosed(this)
		})
	}

	#showPage(view: WebContentsView): void {
		if (this.#shown && this.#shown !== view) this.#hidePage(this.#shown)
		this.#shown = view
		view.setBounds(pageBounds(this.window.getContentBounds()))
		this.window.contentView.addChildView(view)
		this.#overlay.raise()
	}

	#hidePage(view: WebContentsView): void {
		if (this.#shown === view) this.#shown = undefined
		if (this.window.isDestroyed() || !this.window.contentView.children.includes(view)) return
		this.window.contentView.removeChildView(view)
	}

	#layout(): void {
		this.#shown?.setBounds(pageBounds(this.window.getContentBounds()))
		this.#overlay.layout()
	}

	#focusAddress(): void {
		this.window.webContents.focus()
		this.window.webContents.send('chrome:focus-address')
	}

	#sendState(): void {
		if (this.window.isDestroyed()) return
		this.window.webContents.send('tabs:state', this.tabs.state())
	}
}
