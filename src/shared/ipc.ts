import type { CommandId } from './commands/registry'
import type { TabId, TabRecord } from './model/tab'

/** A tab as the chrome draws it: the record plus state that only exists while a renderer is attached. */
export interface TabView extends TabRecord {
	readonly loading: boolean
	readonly canGoBack: boolean
	readonly canGoForward: boolean
}

export interface TabsState {
	readonly tabs: readonly TabView[]
	readonly activeId: TabId | undefined
}

export type CommandOutcome =
	| { readonly status: 'done' }
	/** Nothing to act on right now (Back with no history); keyboard input passes through to the page. */
	| { readonly status: 'not-applicable' }
	| { readonly status: 'not-implemented'; readonly label: string }

export interface Notice {
	readonly id: number
	readonly text: string
}

export interface TitleBarColors {
	readonly symbolColor: string
}

/** A point in the chrome renderer's CSS pixels, where a native menu should open. */
export interface MenuAnchor {
	readonly x: number
	readonly y: number
}

/**
 * Heights of the chrome bands above the page, in DIPs. Their sum is a multiple
 * of 4 so the page's top edge lands on a whole device pixel at 100, 125, 150,
 * 175 and 200% scaling; a web view on a half pixel gets its whole layer
 * resampled (Krackn's pixel-alignment gotcha).
 */
export const CHROME_LAYOUT = {
	tabStripHeight: 40,
	toolbarHeight: 40,
} as const

export const CHROME_HEIGHT = CHROME_LAYOUT.tabStripHeight + CHROME_LAYOUT.toolbarHeight

/** Renderer → main requests, answered with a promise. */
export interface InvokeContract {
	'command:run': { args: [id: CommandId]; result: CommandOutcome }
	'tabs:get': { args: []; result: TabsState }
	'tabs:activate': { args: [id: TabId]; result: void }
	'tabs:close': { args: [id: TabId]; result: void }
	'tabs:navigate': { args: [id: TabId, input: string]; result: void }
	'tabs:move': { args: [id: TabId, index: number]; result: void }
	'tabs:pin': { args: [id: TabId]; result: void }
	'tabs:unpin': { args: [id: TabId]; result: void }
	'chrome:titlebar-colors': { args: [colors: TitleBarColors]; result: void }
	'chrome:app-menu': { args: [anchor: MenuAnchor]; result: void }
	'chrome:tab-menu': { args: [id: TabId, anchor: MenuAnchor]; result: void }
}

/** Main → renderer pushes. `tabs:state` is coalesced to at most one per frame. */
export interface EventContract {
	'tabs:state': TabsState
	'chrome:focus-address': undefined
	'overlay:notice': Notice | null
}

export type InvokeChannel = keyof InvokeContract
export type EventChannel = keyof EventContract

export type Unsubscribe = () => void

/** The whole surface the preload exposes as `window.zing`. Nothing else crosses the bridge. */
export interface ZingApi {
	runCommand(id: CommandId): Promise<CommandOutcome>
	getTabs(): Promise<TabsState>
	activateTab(id: TabId): Promise<void>
	closeTab(id: TabId): Promise<void>
	navigateTab(id: TabId, input: string): Promise<void>
	moveTab(id: TabId, index: number): Promise<void>
	pinTab(id: TabId): Promise<void>
	unpinTab(id: TabId): Promise<void>
	setTitleBarColors(colors: TitleBarColors): Promise<void>
	openAppMenu(anchor: MenuAnchor): Promise<void>
	openTabMenu(id: TabId, anchor: MenuAnchor): Promise<void>
	onTabsState(listener: (state: TabsState) => void): Unsubscribe
	onFocusAddress(listener: () => void): Unsubscribe
	onNotice(listener: (notice: Notice | null) => void): Unsubscribe
}
