import { app, dialog, type BrowserWindow, type Menu, type MessageBoxOptions, type Session } from 'electron'
import type { SettingsStore } from '@shared/settings'
import type { AccountStore } from './account'
import type { LibraryStore } from './library-store'
import type { SiteDataKeeper } from './privacy'
import { needsQuitConfirmation } from './quit-policy'
import { ZingWindow } from './zing-window'

export interface WindowManagerOptions {
	readonly session: Session
	readonly settings: SettingsStore
	readonly appMenu: () => Menu
	readonly siteData: SiteDataKeeper | undefined
	readonly account: AccountStore
	readonly library: LibraryStore
	/** Called for each window as it is created, e.g. to attach a taskbar icon. */
	readonly onCreated?: (window: ZingWindow) => void
}

/**
 * Every open browser window, oldest first. Each window owns its own tab
 * service; they share the one in-memory session.
 */
export class WindowManager {
	readonly #windows: ZingWindow[] = []
	readonly #focusOrder: ZingWindow[] = []
	readonly #options: WindowManagerOptions

	constructor(options: WindowManagerOptions) {
		this.#options = options
	}

	get all(): readonly ZingWindow[] {
		return this.#windows
	}

	/** The oldest open window. The only one that may save the session, so two windows never overwrite each other. */
	get primary(): ZingWindow | undefined {
		return this.#windows[0]
	}

	isPrimary(window: ZingWindow): boolean {
		return this.primary === window
	}

	find(predicate: (window: ZingWindow) => boolean): ZingWindow | undefined {
		return this.#windows.find(predicate)
	}

	mostRecent(): ZingWindow | undefined {
		return this.#focusOrder.at(-1) ?? this.#windows.at(-1)
	}

	totalTabs(): number {
		return this.#windows.reduce((sum, w) => sum + w.tabs.state().tabs.length, 0)
	}

	create(): ZingWindow {
		const window = new ZingWindow({
			session: this.#options.session,
			settings: this.#options.settings,
			appMenu: this.#options.appMenu,
			siteData: this.#options.siteData,
			library: this.#options.library,
			account: this.#options.account,
			newWindow: () => void this.create(),
			isPrimary: (w) => this.isPrimary(w),
			onClosed: (w) => this.#forget(w),
		})
		this.#windows.push(window)
		this.#focusOrder.push(window)
		window.window.on('focus', () => this.#touch(window))
		this.#options.onCreated?.(window)
		return window
	}

	/** Opens a link from another app as a tab in the window used last, bringing it to the front. */
	openLink(url: string): void {
		const target = this.mostRecent() ?? this.create()
		target.openLink(url)
		target.reveal()
	}

	#touch(window: ZingWindow): void {
		const at = this.#focusOrder.indexOf(window)
		if (at >= 0) this.#focusOrder.splice(at, 1)
		this.#focusOrder.push(window)
	}

	#forget(window: ZingWindow): void {
		for (const list of [this.#windows, this.#focusOrder]) {
			const at = list.indexOf(window)
			if (at >= 0) list.splice(at, 1)
		}
	}
}

export type QuitPrompt = (parent: BrowserWindow | undefined, openTabs: number) => Promise<boolean>

export const askToQuit: QuitPrompt = async (parent, openTabs) => {
	const options: MessageBoxOptions = {
		type: 'question',
		buttons: ['Quit', 'Cancel'],
		defaultId: 1,
		cancelId: 1,
		title: 'Quit Zing',
		message: 'Quit Zing?',
		detail: `${openTabs} tabs are open. Zing keeps nothing, so their pages and sign-ins are gone once it quits.`,
	}
	const { response } = parent ? await dialog.showMessageBox(parent, options) : await dialog.showMessageBox(options)
	return response === 0
}

/**
 * With `confirmQuit` on, quitting from the menu or by closing the last window
 * asks first when more than one tab is open. Closing one of several windows
 * does not quit, so it never asks.
 */
export function installQuitConfirmation(
	windows: WindowManager,
	settings: SettingsStore,
	onError: (error: unknown) => void,
	prompt: QuitPrompt = askToQuit,
): void {
	let confirmed = false
	let asking = false

	const mustAsk = (): boolean => !confirmed && needsQuitConfirmation(settings.get('confirmQuit'), windows.totalTabs())

	function ask(parent: BrowserWindow | undefined, proceed: () => void): void {
		if (asking) return
		asking = true
		prompt(parent, windows.totalTabs())
			.then((ok) => {
				if (!ok) return
				confirmed = true
				proceed()
			})
			.catch(onError)
			.finally(() => {
				asking = false
			})
	}

	app.on('before-quit', (event) => {
		if (!mustAsk()) return
		event.preventDefault()
		ask(windows.mostRecent()?.window, () => app.quit())
	})

	app.on('browser-window-created', (_event, window) => {
		window.on('close', (event) => {
			const zing = windows.find((w) => w.window === window)
			if (!zing || windows.all.length > 1 || !mustAsk()) return
			event.preventDefault()
			ask(window, () => window.close())
		})
	})
}
