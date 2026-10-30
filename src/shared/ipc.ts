import type { FindDirection, HistoryDirection, PageUiState } from './browser/pageUi'
import type { CommandId } from './commands/registry'
import type { BrowserLibrary } from './library/library'
import type { TabId, TabRecord } from './model/tab'
import type { TabOrganization } from './browser/tabOrganizer'
import type { Settings, SettingKey } from './settings'
import type {
	AccountOutcome,
	AccountState,
	OpenPanelOutcome,
	Point,
	SettingsCategoryId,
	ShellMenuKind,
	ShellState,
	ToolPanelsState,
} from './shell/shellState'

/** A tab as the chrome draws it: the record plus state that only exists while a renderer is attached. */
export interface TabView extends TabRecord {
	/** The memory-only favicon (a small data URL) once it has loaded. */
	readonly icon?: string
	/** Its renderer was discarded to save memory; the strip can draw it dimmed. Activating wakes it. */
	readonly sleeping?: boolean
	readonly loading: boolean
	readonly canGoBack: boolean
	readonly canGoForward: boolean
}

export interface TabList {
	readonly tabs: readonly TabView[]
	readonly activeId: TabId | undefined
}

/** The tab list plus how the strip should arrange and draw it. */
export interface TabsState extends TabList {
	readonly organization: TabOrganization
	/** The Dropdown tab style: sites with several pages become one stack chip. */
	readonly dropdowns: boolean
	/** Key of the stack whose dropdown is open. */
	readonly openStack: string | undefined
}

/** What the stack dropdown (drawn in the overlay view) shows. */
export interface StackDropdownState {
	readonly key: string
	readonly name: string
	/** Where the stack chip's centre sits, in window x. */
	readonly anchorX: number
	readonly tabs: readonly TabView[]
	readonly activeId: TabId | undefined
}

export type DragPresenceSource = 'chip' | 'panel'

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
	/** Only present while the Favourites bar shows; 80 + 28 = 108 is still a multiple of 4. */
	favouritesBarHeight: 28,
} as const

export const CHROME_HEIGHT = CHROME_LAYOUT.tabStripHeight + CHROME_LAYOUT.toolbarHeight

/** Everything above the page: the strip, the toolbar and, when shown, the Favourites bar. */
export function chromeHeight(favouritesBar: boolean): number {
	return CHROME_HEIGHT + (favouritesBar ? CHROME_LAYOUT.favouritesBarHeight : 0)
}

export type LibraryPanel = 'bookmarks' | 'readingList'

/** The bookmarks side panel docks beside the page, so the page view starts this far from the left edge. */
export const LIBRARY_PANEL_WIDTH = 320

export interface LibraryViewState {
	readonly library: BrowserLibrary
	readonly panel: LibraryPanel | null
	readonly favouritesBar: boolean
	/** Where the panel starts: the width of the pinned sidebar beside it. */
	readonly offset: number
}

/** Edits the side panel and the Favourites bar can ask for. `to` is an insertion point in the item's own list. */
export type LibraryAction =
	| { readonly type: 'rename'; readonly id: string; readonly title: string }
	| { readonly type: 'remove'; readonly ids: readonly string[] }
	/** Groups the given items into a new folder; with none, adds an empty one. Answers with the folder's id. */
	| { readonly type: 'newFolder'; readonly ids: readonly string[] }
	| { readonly type: 'move'; readonly id: string; readonly to: number }
	| { readonly type: 'setRead'; readonly id: string; readonly read: boolean }

export type LibraryOpenTarget = 'current' | 'tab'

export interface OverviewTab {
	readonly id: TabId
	readonly title: string
	readonly url: string
	readonly icon?: string
	/** A small JPEG data URL; absent for a tab that has no live page to capture. */
	readonly thumbnail?: string
	readonly active: boolean
}

export interface OverviewState {
	readonly tabs: readonly OverviewTab[]
}

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
	'tabs:move-stack': { args: [key: string, index: number]; result: void }
	'tabs:move-within': { args: [id: TabId, key: string, index: number]; result: void }
	'tabs:close-others': { args: [id: TabId]; result: void }
	'tabs:set-organization': { args: [organization: TabOrganization]; result: void }
	'tabs:set-dropdowns': { args: [on: boolean]; result: void }
	'stack:toggle': { args: [key: string, anchorX: number]; result: void }
	'stack:open-for-drag': { args: [key: string, anchorX: number]; result: void }
	'stack:close': { args: []; result: void }
	'stack:current': { args: []; result: StackDropdownState | null }
	'stack:drag-presence': { args: [source: DragPresenceSource, inside: boolean]; result: void }
	'chrome:stack-menu': { args: [key: string, anchor: MenuAnchor]; result: void }
	'chrome:strip-menu': { args: [anchor: MenuAnchor]; result: void }
	'chrome:titlebar-colors': { args: [colors: TitleBarColors]; result: void }
	'chrome:app-menu': { args: [anchor: MenuAnchor]; result: void }
	'chrome:tab-menu': { args: [id: TabId, anchor: MenuAnchor]; result: void }
	/** Opens Windows' Default apps page; never sets the default itself. */
	'default-browser:open-settings': { args: []; result: void }
	'chrome:history-menu': { args: [direction: HistoryDirection, anchor: MenuAnchor]; result: void }
	'pageui:get': { args: []; result: PageUiState }
	'pageui:height': { args: [height: number]; result: void }
	'find:update': { args: [text: string]; result: void }
	'find:step': { args: [direction: FindDirection]; result: void }
	'find:close': { args: []; result: void }
	'downloads:open': { args: [id: number]; result: void }
	'downloads:show': { args: [id: number]; result: void }
	'downloads:cancel': { args: [id: number]; result: void }
	'downloads:clear': { args: []; result: void }
	'downloads:dismiss': { args: []; result: void }
	'permission:allow': { args: [id: number]; result: void }
	'permission:deny': { args: [id: number]; result: void }
	'library:get': { args: []; result: LibraryViewState }
	'library:apply': { args: [action: LibraryAction]; result: string | undefined }
	'library:open': { args: [id: string, target: LibraryOpenTarget]; result: void }
	'library:open-all': { args: [id: string]; result: void }
	'library:panel': { args: [panel: LibraryPanel | null]; result: void }
	'library:folder-menu': { args: [id: string, anchor: MenuAnchor]; result: void }
	'library:item-menu': { args: [id: string, anchor: MenuAnchor]; result: void }
	'overview:get': { args: []; result: OverviewState | null }
	'settings:get': { args: []; result: Settings }
	'settings:set': { args: [key: SettingKey, value: unknown]; result: void }
	'shell:get': { args: []; result: ShellState }
	'shell:open-settings': { args: [category?: SettingsCategoryId]; result: void }
	'shell:close-settings': { args: []; result: void }
	'shell:toolbox': { args: []; result: void }
	'shell:menu': { args: [kind: ShellMenuKind, id: string, anchor: MenuAnchor]; result: void }
	'shell:clear-session': { args: []; result: void }
	'toolpanel:get': { args: []; result: ToolPanelsState }
	/** `at` is in screen coordinates (a pointer event's screenX/screenY). */
	'toolpanel:open': { args: [content: string, at: Point]; result: OpenPanelOutcome }
	'toolpanel:close': { args: [id: string]; result: void }
	'toolpanel:minimize': { args: [id: string]; result: void }
	'toolpanel:restore': { args: [id: string]; result: OpenPanelOutcome }
	'toolpanel:focus': { args: [id: string]; result: void }
	'toolpanel:begin-drag': { args: [id: string]; result: void }
	/** `origin` is the panel's top-left in window content coordinates. */
	'toolpanel:end-drag': { args: [id: string, origin: Point]; result: void }
	'account:get': { args: []; result: AccountState }
	'account:sign-up': { args: [username: string, displayName: string, email: string, password: string]; result: AccountOutcome }
	'account:sign-in': { args: [password: string]; result: AccountOutcome }
	'account:reset-password': { args: [password: string]; result: AccountOutcome }
	'account:sign-out': { args: []; result: AccountOutcome }
	'account:continue-as-guest': { args: []; result: AccountOutcome }
	'account:update-profile': { args: [displayName: string, email: string]; result: AccountOutcome }
	'account:require-password': { args: [required: boolean]; result: AccountOutcome }
	'account:delete': { args: []; result: AccountOutcome }
	'overview:close': { args: []; result: void }
}

/** Main → renderer pushes. `tabs:state` is coalesced to at most one per frame. */
export interface EventContract {
	'tabs:state': TabsState
	'chrome:focus-address': undefined
	'stack:dropdown': StackDropdownState | null
	'overlay:notice': Notice | null
	'pageui:state': PageUiState
	'library:state': LibraryViewState
	'overlay:overview': OverviewState | null
	'settings:changed': Settings
	'shell:state': ShellState
	'toolpanel:state': ToolPanelsState
	'account:state': AccountState
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
	moveStack(key: string, index: number): Promise<void>
	moveWithinStack(id: TabId, key: string, index: number): Promise<void>
	closeOtherTabs(id: TabId): Promise<void>
	setTabOrganization(organization: TabOrganization): Promise<void>
	setTabDropdowns(on: boolean): Promise<void>
	toggleStack(key: string, anchorX: number): Promise<void>
	openStackForDrag(key: string, anchorX: number): Promise<void>
	closeStack(): Promise<void>
	currentStack(): Promise<StackDropdownState | null>
	stackDragPresence(source: DragPresenceSource, inside: boolean): Promise<void>
	openStackMenu(key: string, anchor: MenuAnchor): Promise<void>
	openStripMenu(anchor: MenuAnchor): Promise<void>
	setTitleBarColors(colors: TitleBarColors): Promise<void>
	openAppMenu(anchor: MenuAnchor): Promise<void>
	openTabMenu(id: TabId, anchor: MenuAnchor): Promise<void>
	openDefaultAppsSettings(): Promise<void>
	openHistoryMenu(direction: HistoryDirection, anchor: MenuAnchor): Promise<void>
	getPageUi(): Promise<PageUiState>
	setPageUiHeight(height: number): Promise<void>
	updateFind(text: string): Promise<void>
	stepFind(direction: FindDirection): Promise<void>
	closeFind(): Promise<void>
	openDownload(id: number): Promise<void>
	showDownload(id: number): Promise<void>
	cancelDownload(id: number): Promise<void>
	clearDownloads(): Promise<void>
	dismissDownloads(): Promise<void>
	allowPermission(id: number): Promise<void>
	denyPermission(id: number): Promise<void>
	getLibrary(): Promise<LibraryViewState>
	applyLibrary(action: LibraryAction): Promise<string | undefined>
	openLibraryItem(id: string, target: LibraryOpenTarget): Promise<void>
	openAllInFolder(id: string): Promise<void>
	setLibraryPanel(panel: LibraryPanel | null): Promise<void>
	openFolderMenu(id: string, anchor: MenuAnchor): Promise<void>
	openLibraryItemMenu(id: string, anchor: MenuAnchor): Promise<void>
	getOverview(): Promise<OverviewState | null>
	closeOverview(): Promise<void>
	onTabsState(listener: (state: TabsState) => void): Unsubscribe
	onFocusAddress(listener: () => void): Unsubscribe
	onStackDropdown(listener: (state: StackDropdownState | null) => void): Unsubscribe
	onNotice(listener: (notice: Notice | null) => void): Unsubscribe
	onPageUiState(listener: (state: PageUiState) => void): Unsubscribe
	onLibraryState(listener: (state: LibraryViewState) => void): Unsubscribe
	onOverview(listener: (state: OverviewState | null) => void): Unsubscribe
	getSettings(): Promise<Settings>
	setSetting(key: SettingKey, value: unknown): Promise<void>
	onSettingsChanged(listener: (settings: Settings) => void): Unsubscribe
	getShell(): Promise<ShellState>
	openSettings(category?: SettingsCategoryId): Promise<void>
	closeSettings(): Promise<void>
	openToolbox(): Promise<void>
	openShellMenu(kind: ShellMenuKind, id: string, anchor: MenuAnchor): Promise<void>
	clearBrowsingSession(): Promise<void>
	onShellState(listener: (state: ShellState) => void): Unsubscribe
	getToolPanels(): Promise<ToolPanelsState>
	openPanel(content: string, at: Point): Promise<OpenPanelOutcome>
	closePanel(id: string): Promise<void>
	minimizePanel(id: string): Promise<void>
	restorePanel(id: string): Promise<OpenPanelOutcome>
	focusPanel(id: string): Promise<void>
	beginPanelDrag(id: string): Promise<void>
	endPanelDrag(id: string, origin: Point): Promise<void>
	onToolPanelsState(listener: (state: ToolPanelsState) => void): Unsubscribe
	getAccount(): Promise<AccountState>
	signUp(username: string, displayName: string, email: string, password: string): Promise<AccountOutcome>
	signIn(password: string): Promise<AccountOutcome>
	resetPassword(password: string): Promise<AccountOutcome>
	signOut(): Promise<AccountOutcome>
	continueAsGuest(): Promise<AccountOutcome>
	updateProfile(displayName: string, email: string): Promise<AccountOutcome>
	setRequirePassword(required: boolean): Promise<AccountOutcome>
	deleteAccount(): Promise<AccountOutcome>
	onAccountState(listener: (state: AccountState) => void): Unsubscribe
}
