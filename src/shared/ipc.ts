import type { EngineInfo, EngineProgress, EnsureOutcome, YtDlpUpdate } from './engines/engineInfo'
import type { SessionKindName, ViewerOpened } from './convert/viewable'
import type { EngineExit, EngineJobSpec, EngineOutput, FileKind, PickedFile } from './engines/engineJob'
import type { AccountPickerState, FillOutcome, FindDirection, HistoryDirection, PageUiState, SaveAnswer, SaveOutcome } from './browser/pageUi'
import type { CommandId } from './commands/registry'
import type { PeekState } from './browser/peek'
import type { BrowserLibrary } from './library/library'
import type { TabId, TabRecord } from './model/tab'
import type { TabOrganization } from './browser/tabOrganizer'
import type { Settings, SettingKey } from './settings'
import type { DiagnosticReport } from './unblock/blockDiagnostics'
import type { DnsOutcome, DnsPreview } from './unblock/dnsSetup'
import type { RelayAttemptEvent, RelayOpenResult } from './unblock/relayOpen'
import type { LoginEdit, LoginSummary, NeverSave, NewLogin, PasswordHistoryEntry, RecoveryKeyShown, SyncState, VaultImportResult, VaultState } from './vault/vault'
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

/** The page's render process exited ('crashed') or stopped answering ('unresponsive'). Host: PageFault. */
export type TabPageState = 'crashed' | 'unresponsive'

/** Where updating stands (Zing.Core Updates.cs). 'unavailable' is a build that isn't installed: it never checks. */
export type UpdateStatus = 'idle' | 'checking' | 'downloading' | 'ready' | 'upToDate' | 'failed' | 'unavailable'

export interface UpdateState {
	readonly status: UpdateStatus
	/** Empty in a build that isn't installed. */
	readonly currentVersion: string
	/** The downloaded version, while the status is 'ready'. */
	readonly readyVersion?: string
}

/** Zing.Core DefaultBrowserState: registered is an installed Zing (an unpackaged build isn't); isDefault is judged on https. */
export interface DefaultBrowserState {
	readonly registered: boolean
	readonly isDefault: boolean
}

/** A tab as the chrome draws it: the record plus state that only exists while a renderer is attached. */
export interface TabView extends TabRecord {
	/** The memory-only favicon (a small data URL) once it has loaded. */
	readonly icon?: string
	/** Its renderer was discarded to save memory; the strip can draw it dimmed. Activating wakes it. */
	readonly sleeping?: boolean
	/** Why its page isn't working; absent while it is. The tab keeps its record, and Reload brings the page back. */
	readonly pageState?: TabPageState
	/** The saved item (Favourites, Pinned) this tab belongs to; the rail draws it there, not among today's tabs. */
	readonly itemId?: string
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
	/** This window's splits (split view), each a layout over tab ids. */
	readonly splits?: readonly SplitView[]
	/** The active tab's split on screen: where its panes are, in the window's content, in DIPs. */
	readonly shownSplit?: ShownSplit
	/** Where pages go in the window's content, in DIPs: a tab dragged there can split the page. */
	readonly pageArea?: SplitRect
}

export interface SplitRect {
	readonly x: number
	readonly y: number
	readonly width: number
	readonly height: number
}

export interface SplitView {
	readonly id: string
	/** 'row' puts the panes side by side, 'column' one above the other. */
	readonly orientation: 'row' | 'column'
	readonly tabIds: readonly TabId[]
	readonly ratios: readonly number[]
}

export interface ShownSplit {
	readonly id: string
	readonly rects: readonly SplitRect[]
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

/** What the failed-load offer's buttons open. */
export type OfferTool = 'openLink' | 'diagnose'

export interface Notice {
	readonly id: number
	readonly text: string
	/** Present for the offer after a page failed like a block: it stays until chosen or dismissed and carries the buttons. */
	readonly offer?: { readonly address: string }
	/** Present for the offer to make Zing the default browser: "Make default" and "Don't ask again". Left alone, it times out and comes back next launch. */
	readonly defaultBrowser?: true
}

export interface TitleBarColors {
	readonly symbolColor: string
	readonly background: string
	readonly hover: string
	readonly pressed: string
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
	// Windows draws its caption buttons 32 or 48 tall, and they share a row with these.
	tabStripHeight: 48,
	toolbarHeight: 48,
	/** Only present while the Favourites bar shows; 96 + 40 = 136 is still a multiple of 4. */
	favouritesBarHeight: 40,
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
	/** Bookmark id to its site's icon (a PNG data URL), kept once the person visited that page; bookmarks never visited have none. */
	readonly icons: Readonly<Record<string, string>>
	readonly panel: LibraryPanel | undefined
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
	/** Moves a bookmark into a folder (its own, or another), before the child now at `to`. */
	| { readonly type: 'moveInto'; readonly id: string; readonly parent: string; readonly to: number }
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

/** An error a Zing view hit, sent to the host so it is recorded like the host's own (e2e fails on it). */
export interface RendererErrorReport {
	readonly kind: 'error' | 'unhandledrejection' | 'bridge'
	readonly message: string
	readonly stack?: string
	/** The view's root, e.g. `chrome` or `overlay-stack`. */
	readonly view?: string
}

export interface ImportSourceView {
	id: string
	browser: 'chrome' | 'edge' | 'brave' | 'arc'
	display: string
	bookmarks?: number
	error?: string
	/** Whether Zing can also import this browser's passwords (Chrome, Edge and Brave). */
	passwords: boolean
}

export interface BookmarkImportOutcome {
	folderTitle: string
	addedToBar: number
	addedToFolder: number
	/** Why each item that couldn't be imported was left out. */
	skipped: string[]
}

/**
 * Renderer → host requests, answered with a promise. The C# host registers a
 * handler for each by name (ChromeApi, plus a few in MainWindow); the list is
 * generated into host/Zing.Core/Defaults/channels.json and checked on both sides.
 */
export interface InvokeContract {
	'app:report-error': { args: [report: RendererErrorReport]; result: void }
	'command:run': { args: [id: CommandId]; result: CommandOutcome }
	'tabs:get': { args: []; result: TabsState }
	'tabs:activate': { args: [id: TabId]; result: void }
	'tabs:close': { args: [id: TabId]; result: void }
	'tabs:navigate': { args: [id: TabId, input: string]; result: void }
	/** A new tab straight at a web address (a Toolbox link tile): no blank tab first, so Back has nothing behind it. */
	'tabs:open-link': { args: [url: string]; result: void }
	'tabs:move': { args: [id: TabId, index: number]; result: void }
	'tabs:pin': { args: [id: TabId]; result: void }
	'tabs:unpin': { args: [id: TabId]; result: void }
	/** Switches to a saved item's tab (Favourites, Pinned), or opens one at its address. */
	'tabs:open-item': { args: [itemId: string]; result: void }
	/** Puts `other` beside `id`: into its split, or a new side-by-side split of the two. */
	'split:with': { args: [id: TabId, other: TabId]; result: void }
	/** A tab dropped on a page's edge: beside that page, on that side. */
	'split:drop': { args: [target: TabId, dropped: TabId, edge: 'left' | 'right' | 'top' | 'bottom']; result: void }
	'split:remove': { args: [id: TabId]; result: void }
	/** Where a tab dragged over a page would land, highlighted by the surface view; null once the drag leaves or ends. */
	'split:drop-zone': { args: [zone: SplitRect | null]; result: void }
	/** New pane sizes (a divider dragged); `length` is the page area's along the split. */
	'split:ratios': { args: [splitId: string, ratios: number[], length: number]; result: void }
	/** Saves a tab into a folder of Favourites or Pinned before the child now at `at`, tied to it; a saved tab's item moves there. */
	'tabs:save': { args: [id: TabId, folderId: string, at: number]; result: void }
	'tabs:move-stack': { args: [key: string, index: number]; result: void }
	'tabs:move-within': { args: [id: TabId, key: string, index: number]; result: void }
	'tabs:close-others': { args: [id: TabId]; result: void }
	'tabs:set-organization': { args: [organization: TabOrganization]; result: void }
	'tabs:set-dropdowns': { args: [on: boolean]; result: void }
	'stack:toggle': { args: [key: string, anchorX: number]; result: void }
	'stack:open-for-drag': { args: [key: string, anchorX: number]; result: void }
	'stack:close': { args: []; result: void }
	'stack:current': { args: []; result: StackDropdownState | undefined }
	'stack:drag-presence': { args: [source: DragPresenceSource, inside: boolean]; result: void }
	'chrome:stack-menu': { args: [key: string, anchor: MenuAnchor]; result: void }
	'chrome:strip-menu': { args: [anchor: MenuAnchor]; result: void }
	'chrome:titlebar-colors': { args: [colors: TitleBarColors | null]; result: void }
	'chrome:app-menu': { args: [anchor: MenuAnchor]; result: void }
	'chrome:tab-menu': { args: [id: TabId, anchor: MenuAnchor]; result: void }
	/** Opens Windows' Default apps page; never sets the default itself. */
	'default-browser:open-settings': { args: []; result: void }
	'chrome:history-menu': { args: [direction: HistoryDirection, anchor: MenuAnchor]; result: void }
	'pageui:get': { args: []; result: PageUiState }
	'pageui:height': { args: [height: number]; result: void }
	/** The account picker on screen, or null. Usernames only, never a password; the page itself never sees them. */
	'pageui:picker-get': { args: []; result: AccountPickerState | null }
	/** The picker view measured what it shows. */
	'pageui:picker-height': { args: [height: number]; result: void }
	/** Esc in the picker: closes it and gives the keyboard back to the page. */
	'pageui:picker-close': { args: []; result: void }
	/** The link preview's buttons, or null when no preview is open. */
	'peek:get': { args: []; result: PeekState | null }
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
	'overview:get': { args: []; result: OverviewState | undefined }
	'settings:get': { args: []; result: Settings }
	'settings:set': { args: [key: SettingKey, value: unknown]; result: void }
	'shell:get': { args: []; result: ShellState }
	'shell:open-settings': { args: [category?: SettingsCategoryId]; result: void }
	'shell:close-settings': { args: []; result: void }
	'shell:toolbox': { args: []; result: void }
	'shell:close-toolbox': { args: []; result: void }
	'shell:menu': { args: [kind: ShellMenuKind, id: string, anchor: MenuAnchor]; result: void }
	'shell:clear-session': { args: []; result: void }
	'toolpanel:get': { args: []; result: ToolPanelsState }
	/** `at` is in screen coordinates (a pointer event's screenX/screenY). */
	'toolpanel:open': { args: [content: string, at: Point]; result: OpenPanelOutcome }
	/** Opens (or raises) the panel in the middle of the page with an address filled in; the field is only filled, starting anything is a click in the tool. */
	'toolpanel:open-with-input': { args: [content: string, input: string]; result: OpenPanelOutcome }
	'toolpanel:close': { args: [id: string]; result: void }
	'toolpanel:minimize': { args: [id: string]; result: void }
	'toolpanel:restore': { args: [id: string]; result: OpenPanelOutcome }
	'toolpanel:focus': { args: [id: string]; result: void }
	'toolpanel:begin-drag': { args: [id: string]; result: void }
	/** `origin` is the panel's top-left in window content coordinates. */
	'toolpanel:end-drag': { args: [id: string, origin: Point]; result: void }
	/** Lowercase hex digests. `algorithm` is a `HashAlgorithm` ("MD5", "SHA-1", "SHA-256", "SHA-512"); the host also answers MD5, which SubtleCrypto lacks. */
	'hash:text': { args: [text: string, algorithm: string]; result: string }
	/** A file by its `files:pick` token, never a path. */
	'hash:file': { args: [token: string, algorithm: string]; result: string }
	'hash:hmac': { args: [message: string, key: string, algorithm: string]; result: string }
	/** Every engine with its size and whether it is downloaded. Reads the disk; makes no request. */
	'engines:list': { args: []; result: EngineInfo[] }
	/** Downloads one, only because a person asked. Pushes `engines:progress`; rejects with the engine's name and the address to fetch it by hand. */
	'engines:ensure': { args: [id: string]; result: EnsureOutcome }
	/** Stops this window's download of one engine. `engines:ensure` then answers 'cancelled' once nothing is left on disk. */
	'engines:cancel': { args: [id: string]; result: void }
	/** Rejects with EngineInUseException while a tool is using it. */
	'engines:remove': { args: [id: string]; result: void }
	/** Checks the newest yt-dlp release against its published SHA2-256SUMS; a mismatch keeps the old version. */
	'engines:update-ytdlp': { args: []; result: YtDlpUpdate }
	/**
	 * Starts an engine on a job owned by an open panel and answers with the job's id. Closing that panel (not minimizing it)
	 * or the window stops the job. Pushes `engines:output` and, once, `engines:exit`. Rejects when the engine isn't downloaded
	 * (it never downloads), when 3 jobs already run, or with the reason an argument or file was refused.
	 */
	'engines:run': { args: [engineId: string, panelId: string, job: EngineJobSpec]; result: string }
	/** Stops a job of this window; its outputs are discarded. (`engines:cancel` stops a download.) */
	'engines:cancel-job': { args: [jobId: string]; result: void }
	/**
	 * Saves the active tab's HTML (only what is on the page, no images or styles) to Downloads (a taken name is numbered unless "Never overwrite files" is off), and
	 * answers with the file's name and a token. Rejects for a page that isn't http or https, one in reader mode, or a very
	 * large one. A page view cannot call this: it has no host channel.
	 */
	'page:save': { args: []; result: PickedFile }
	/**
	 * A session that only takes results (`PUT https://view.zing/save/<id>/<name>`, for the formats of `kinds`) and serves no file. It
	 * replaces the panel's earlier session, shares the save quota and ends with the panel. For a tool that has read its file and
	 * must still be able to save when that file is gone.
	 */
	'session:open-save': { args: [panelId: string, kinds: readonly SessionKindName[]]; result: ViewerOpened }
	/** The native open dialog; an empty list if the person cancelled. */
	'files:pick': { args: [kinds: readonly FileKind[]]; result: PickedFile[] }
	/**
	 * Puts the file a token stands for on the clipboard as a file (CF_HDROP), so Explorer can paste it. Takes a token, never a
	 * path: the host looks the path up, and rejects a token it never issued here or a file that has gone.
	 */
	'files:copy-to-clipboard': { args: [token: string]; result: void }
	/**
	 * Convert's HTML to PDF: Chromium prints the HTML file the token stands for, with scripts and the network off, into the
	 * Downloads folder under `outputName` (a plain name ending in .pdf; numbered if taken). Answers with the PDF's name and a
	 * token. Rejects for a file that isn't HTML or is over 1.5 MB.
	 */
	'convert:html-to-pdf': { args: [token: string, outputName: string]; result: PickedFile }
	/**
	 * Opens a picked or dropped file for a tool panel as a read session on the viewer file server (`https://view.zing`). `kinds`
	 * names what the tool takes, from a fixed table: `image` (PNG, JPEG or WebP, up to 120 MB) and `pdf` (`%PDF-` at the start, up
	 * to 50 MB). The host judges the file first bytes and length and rejects anything else (SessionFileRefusedException),
	 * an unknown kind or an unknown panel. The answer's `url` is `GET`able by the tool (a worker included) and nothing else is
	 * revealed: no path, no token. Opening another file in the panel, `viewer:close(id)` or closing the panel revokes it.
	 * The same session takes results: `PUT https://view.zing/save/<id>/<name>` (ViewerFiles.cs).
	 */
	'session:open': { args: [panelId: string, token: string, kinds: readonly SessionKindName[]]; result: ViewerOpened }
	/** The background remover's session: `session:open` with the `image` kind. */
	'bgremove:open': { args: [panelId: string, token: string]; result: ViewerOpened }
	/**
	 * Files dropped on a Zing view. The host reads their paths from the drop itself (`postMessageWithAdditionalObjects`),
	 * so the bridge carries the `File`s beside the message and not in its JSON args; a web page cannot send one.
	 */
	'files:open-dropped': { args: [files: readonly File[]]; result: PickedFile[] }
	/**
	 * Opens a picked or dropped file (by its token) in the viewer panel `panelId`, converting a copy when Chromium can't show it.
	 * One file per panel: it replaces the panel's earlier one. Closing the panel (or the window) ends the conversion and deletes the copy.
	 * Rejects for a format it can't show, an engine that isn't downloaded, or a file that has gone.
	 */
	'viewer:open': { args: [token: string, panelId: string]; result: ViewerOpened }
	/** Forgets the open file: its address answers 404, a running conversion stops and the temporary copy is deleted. */
	'viewer:close': { args: [id: string]; result: void }
	/** Runs the Diagnose ladder for a site name (or a pasted address) with the DoH provider chosen in Settings. Resolver failures are part of the report; a name that isn't a site is an error. */
	'net:diagnose': { args: [host: string]; result: DiagnosticReport }
	/**
	 * The script that sets up encrypted DNS for this PC with the provider chosen in Settings, or, once it has been set up, the script that undoes it.
	 * Reads the adapters; changes nothing and asks for nothing.
	 */
	'dns:preview': { args: []; result: DnsPreview }
	/**
	 * Runs the Apply script a preview showed, as administrator. `token` names that script: the host builds the script itself and, if it is
	 * not the one shown (the adapters changed), runs nothing and answers 'changed' with the new one. The renderer never sends script text.
	 */
	'dns:apply': { args: [token: string]; result: DnsOutcome }
	/** Runs the Undo script a preview showed, from the settings saved before Apply. Same `token` rule. */
	'dns:undo': { args: [token: string]; result: DnsOutcome }
	/**
	 * Tries direct, DoH with IP pinning, the reader relay and the Wayback archive in order, pushing `relay:attempt` for each step
	 * with `runId`, which the caller chooses so overlapping runs can be told apart. The reader relay and the archive wait for the
	 * session's consent (`needsConsent`). Rejects when every method fails, when what was typed is not an address, and after 3 minutes.
	 */
	'relay:open': { args: [input: string, runId: string]; result: RelayOpenResult }
	/** The person said yes to the reader relay and the archive seeing addresses. Holds for this session of the app, in memory only. */
	'relay:allow-third-parties': { args: []; result: void }
	/** A new tab on a page reached through the relay. Rejects an address that isn't https, which the relay scheme can't stand for. */
	'tabs:open-relayed': { args: [url: string]; result: void }
	/** A button of the failed-load offer: opens that tool with the failed address filled in. Starts nothing. */
	'offer:choose': { args: [tool: OfferTool]; result: OpenPanelOutcome }
	'offer:dismiss': { args: []; result: void }
	'account:get': { args: []; result: AccountState }
	'account:sign-up': { args: [username: string, displayName: string, email: string, password: string]; result: AccountOutcome }
	'account:sign-in': { args: [password: string]; result: AccountOutcome }
	'account:reset-password': { args: [password: string]; result: AccountOutcome }
	'account:sign-out': { args: []; result: AccountOutcome }
	'account:continue-as-guest': { args: []; result: AccountOutcome }
	'account:update-profile': { args: [displayName: string, email: string]; result: AccountOutcome }
	'account:require-password': { args: [required: boolean]; result: AccountOutcome }
	'account:delete': { args: []; result: AccountOutcome }
	/**
	 * Changes the Zing password. A wrong current password is a `rejected` outcome. With sync on, the new one must pass the sync
	 * password rules (SyncPasswordTooWeakException, whose message is the first failed rule's sentence) and the keys are re-wrapped
	 * and uploaded before this PC's password changes; keys changed on another PC reject with SyncKeysChangedElsewhereException and
	 * change nothing.
	 */
	'account:change-password': { args: [current: string, next: string]; result: AccountOutcome }
	/** Whether the vault is on, whether Windows Hello is set up, and how many logins it holds. Asks nothing. */
	'vault:get': { args: []; result: VaultState }
	/** Every login without its password, those in Recently deleted included. Never asks Windows Hello. Rejects (VaultOffException) while the vault is off. */
	'vault:summaries': { args: []; result: LoginSummary[] }
	/** One login's password, after Windows Hello. Rejects with HelloCancelledException when the prompt is dismissed, HelloUnavailableException without Hello. */
	'vault:reveal': { args: [id: string]; result: string }
	/** One of a login's old passwords, after Windows Hello; index 0 is the one replaced most recently. */
	'vault:reveal-history': { args: [id: string, index: number]; result: string }
	/** Saves a new login after Windows Hello and answers its ID. The origin may be any address on the site. */
	'vault:save': { args: [login: NewLogin]; result: string }
	/** Edits a login. A `password` asks Windows Hello first and moves the old one into the history; without one there's no prompt. */
	'vault:update': { args: [id: string, edit: LoginEdit]; result: void }
	/** Moves a login to Recently deleted. No prompt. */
	'vault:delete': { args: [id: string]; result: void }
	/** Brings a login back from Recently deleted. No prompt. */
	'vault:restore': { args: [id: string]; result: void }
	/** When each old password was replaced, newest first. No passwords and no prompt. */
	'vault:history': { args: [id: string]; result: PasswordHistoryEntry[] }
	/** Asks Windows Hello unless it was passed in the last 5 minutes, then copies the login's password from the host (kept out of clipboard history and cloud sync) and clears it 30 s later if nothing else was copied. The password never comes back. */
	'vault:copy': { args: [id: string]; result: void }
	/** Imports the CSV a picker token stands for (kind 'csv'), with one Windows Hello prompt for the whole file. Rows it cannot use come back with the reason. */
	'vault:import-csv': { args: [token: string]; result: VaultImportResult }
	/** Always asks Windows Hello, even inside the 5 minutes other uses share, then saves every password into Downloads as "Zing Passwords.csv" in Chrome's format, unencrypted. */
	'vault:export-csv': { args: []; result: PickedFile }
	/** The sites marked "never for this site". No prompt. */
	'vault:never-saves': { args: []; result: NeverSave[] }
	/** Takes a site off that list so Zing offers to save there again. No prompt. */
	'vault:remove-never-save': { args: [origin: string]; result: void }
	/**
	 * The user chose a login in the account picker: asks Windows Hello unless it was passed in the last 5 minutes, then fills that page's field and its other half.
	 * `cancelled` when Hello was cancelled or unavailable (nothing filled, no error); `refused` when the page is no longer
	 * on the origin the picker was shown for; `not-filled` when the field had gone. The picker closes either way.
	 */
	'vault:fill': { args: [pageId: TabId, id: string]; result: FillOutcome }
	/**
	 * The user's answer to the save or update prompt on a tab. `save` (also the update prompt's Update) asks Windows Hello
	 * once; a cancelled Hello stores nothing and closes the prompt without an error. `never` marks the site, for every prompt.
	 */
	'vault:answer-save': { args: [pageId: TabId, answer: SaveAnswer]; result: SaveOutcome }
	/** Whether this PC is signed in to sync, and as whom. Signed out while the vault is off. */
	'sync:get': { args: []; result: SyncState }
	/**
	 * Turn on sync: Google sign-in in a small Zing window, then the sync server's session, kept in the vault. Rejects with
	 * SyncNotSetUpException in a build made without a Google client ID, and VaultOffException while the vault is off.
	 */
	'sync:sign-in': { args: []; result: SyncState }
	/**
	 * Revokes this PC's sync sign-in on the server, then forgets it here; the vault's logins stay. When the server can't be reached it is
	 * still forgotten here, and the answer's notice is `kept-on-server`.
	 */
	'sync:sign-out': { args: []; result: SyncState }
	/**
	 * Turn off sync on this PC (Ruling 68): signs out as sync:sign-out does, then forgets this PC's sync keys, its sync progress and
	 * the sync records of every login. The logins stay, unsynced; the server copy stays for the other PCs. Runs under the vault's key
	 * lock, so it waits for a sync running. After it, the account can be deleted or made again.
	 */
	'sync:turn-off': { args: []; result: SyncState }
	/** Stops waiting for a Google sign-in in progress; that sync:sign-in then resolves with the state as it is, without an error. */
	'sync:cancel-sign-in': { args: []; result: void }
	/**
	 * Turns sync on for the signed-in Google account with the Zing password: wraps the vault's keys, then uploads them, every login
	 * and the manifest. Asks Windows Hello unless it was passed in the last 5 minutes. Answers the recovery key, the only time it is
	 * given without Hello; Settings shows it until sync:confirm-recovery-saved. Rejects with AccountException (no account, wrong
	 * password), SyncPasswordTooWeakException, SyncAlreadySetUpForThisVaultException when the Google account already syncs this vault
	 * (sync was turned off here; restoring from sync turns it on again), or SyncAlreadySetUpElsewhereException when it syncs another.
	 */
	'sync:turn-on': { args: [password: string]; result: RecoveryKeyShown }
	/** Whether the signed-in Google account already syncs a vault: Turn on sync then joins it (sync:join) instead. */
	'sync:server-has-keys': { args: []; result: boolean }
	/**
	 * Turn on sync for a Google account that already syncs another PC: this PC joins it with the sync password, as Restore from sync
	 * does. With an account here, its own password (`current`) is checked first and then replaced by the sync one; with none, the
	 * account is made from the synced profile. Rejects with AccountException (this PC's password is wrong or missing),
	 * SyncJoinWrongPasswordException, SyncRestoreWouldReplaceLoginsException or SyncRestoreWhileSyncingAnotherAccountException.
	 */
	'sync:join': { args: [current: string, password: string]; result: SyncState }
	/** The person confirmed they saved or printed the recovery key, so Settings stops showing it. */
	'sync:confirm-recovery-saved': { args: []; result: SyncState }
	/** The recovery key again, after Windows Hello (unless it was passed in the last 5 minutes). A cancelled Hello rejects and reveals nothing. */
	'sync:show-recovery': { args: []; result: RecoveryKeyShown }
	/**
	 * Upload this PC's copy, after a sync stopped with `server-rolled-back` or `server-withheld-item`: this PC's copy of every login
	 * replaces the server's, and the sync list is written from it. Needs no Windows Hello.
	 */
	'sync:upload-local': { args: []; result: SyncState }
	/**
	 * Restore from sync, at the account gate: signs in with Google as sync:sign-in does, but keeps the sign-in in memory. Nothing on
	 * this PC changes until a restore succeeds: only then is the password vault turned on (sync lives in it) and the sign-in kept.
	 * Rejects with SyncNotSetUpException in a build made without a Google client ID.
	 */
	'sync:restore-sign-in': { args: []; result: SyncState }
	/**
	 * Restores this PC from sync with the Zing password: the synced keys are unwrapped and sealed here (one Windows Hello), and the
	 * account is made from the synced profile, or given the password when this PC has one. `wrong-password` when it doesn't open
	 * them, and then nothing is made. Rejects with SyncNothingToRestoreException, SyncRestoreWouldReplaceLoginsException,
	 * SyncRestoreOverAnotherAccountException (this PC has an account, and the keys are another vault's), SyncRestoreKeysNotThisVaultsException
	 * (they carry its vault ID but not its keys), SyncRestoreWhileSyncingAnotherAccountException (this PC syncs as another Google
	 * account), or SyncKeysFormatException.
	 */
	'sync:restore-password': { args: [password: string]; result: AccountOutcome }
	/**
	 * Checks the recovery key against the synced keys; nothing on this PC changes until sync:restore-new-password. Rejects with
	 * RecoveryKeyFormatException (a mistyped character), WrongRecoveryKeyException, or as sync:restore-password does.
	 */
	'sync:restore-recovery-key': { args: [key: string]; result: void }
	/**
	 * The new Zing password after a recovery key restore: the synced keys are sealed here (one Windows Hello) and wrapped under it with
	 * fresh parameters, and the account is made or given it. Rejects with SyncPasswordTooWeakException (still waiting),
	 * SyncNoNewPasswordNeededException, SyncRestoreOverAnotherAccountException, SyncRestoreKeysNotThisVaultsException,
	 * SyncRestoreWhileSyncingAnotherAccountException or SyncKeysChangedElsewhereException.
	 */
	'sync:restore-new-password': { args: [password: string]; result: AccountOutcome }
	'overview:close': { args: []; result: void }
	'update:get': { args: []; result: UpdateState }
	/** Checks now, whatever the setting says, and answers with where that left things. */
	'update:check': { args: []; result: UpdateState }
	/** Restarts into the downloaded update; does nothing unless the status is 'ready', or once a restart is under way. */
	'update:relaunch': { args: []; result: void }
	/** The pill view measured what it shows, in CSS pixels. */
	'update:pill-size': { args: [width: number, height: number]; result: void }
	'default-browser:get': { args: []; result: DefaultBrowserState }
	/** "Don't ask again": sets defaultBrowserDismissed and takes the offer down. */
	'default-browser:dismiss': { args: []; result: void }
	/** The third-party notices the build generated (THIRD-PARTY-NOTICES.txt). Rejects if the file is missing. */
	'about:credits': { args: []; result: string }
	/** The browsers on this PC whose bookmarks can be imported, one row per profile. A broken profile is a row with `error`, not a rejection. No paths. */
	'import:sources': { args: []; result: ImportSourceView[] }
	/** Merges a source's bookmarks into the library (it only adds). Rejects with UnknownImportSourceException, or with a path-free message when the file can't be read or is damaged. */
	'import:bookmarks': { args: [sourceId: string]; result: BookmarkImportOutcome }
	/** The same for a bookmarks file picked with `pickFiles(['bookmarks'])` (token from `files:pick`); the file's name is the source. */
	'import:bookmarks-file': { args: [token: string]; result: BookmarkImportOutcome }
	/**
	 * Opens the source browser's password export page in that browser and watches the downloads folder for the CSV; the file comes back as
	 * `import:password-export`, or `import:watch-timed-out` after 10 minutes. A new watch ends the old. Rejects with UnknownImportSourceException,
	 * NoPasswordExportException (Arc), BrowserNotInstalledException or DownloadsFolderMissingException.
	 */
	'import:watch-passwords': { args: [sourceId: string]; result: void }
	/** Ends the watch; does nothing when none is running. */
	'import:cancel-watch': { args: []; result: void }
	/** Sends a found export (token from `import:password-export`) to the Recycle Bin. */
	'import:recycle': { args: [token: string]; result: void }
	/** The password import the New Tab card asked Settings to start, once; null when none is waiting. */
	'import:take-pending': { args: []; result: { action: 'passwords'; sourceId: string } | null }
}

/** Host → renderer pushes. The host sends one per change, uncoalesced. */
export interface EventContract {
	'tabs:state': TabsState
	'chrome:focus-address': undefined
	'stack:dropdown': StackDropdownState | undefined
	'overlay:notice': Notice | undefined
	'pageui:state': PageUiState
	'pageui:account-picker': AccountPickerState | null
	'peek:state': PeekState | null
	'library:state': LibraryViewState
	'overlay:overview': OverviewState | undefined
	'settings:changed': Settings
	'shell:state': ShellState
	'toolpanel:state': ToolPanelsState
	'account:state': AccountState
	'vault:state': VaultState
	/** Sync was signed in or out, turned on, or its recovery key confirmed saved, here or in another window, or the server ended the sign-in. */
	'sync:state': SyncState
	'engines:progress': EngineProgress
	'engines:output': EngineOutput
	'engines:exit': EngineExit
	'relay:attempt': RelayAttemptEvent
	'update:state': UpdateState
	/** The watch started by `import:watch-passwords` found the export: a `files:pick`-style grant to import with `vault:import-csv`. */
	'import:password-export': { token: string; name: string }
	'import:watch-timed-out': Record<string, never>
}

export type InvokeChannel = keyof InvokeContract
export type EventChannel = keyof EventContract

export type Unsubscribe = () => void

/**
 * The whole surface Zing's views see as `window.zing`, built over the WebView2
 * bridge (bridge/webviewBridge.ts, zingApi.ts). Nothing else crosses the bridge.
 */
export interface ZingApi {
	runCommand(id: CommandId): Promise<CommandOutcome>
	getTabs(): Promise<TabsState>
	activateTab(id: TabId): Promise<void>
	closeTab(id: TabId): Promise<void>
	navigateTab(id: TabId, input: string): Promise<void>
	openLinkInNewTab(url: string): Promise<void>
	moveTab(id: TabId, index: number): Promise<void>
	pinTab(id: TabId): Promise<void>
	unpinTab(id: TabId): Promise<void>
	openSavedTab(itemId: string): Promise<void>
	splitWith(id: TabId, other: TabId): Promise<void>
	splitDrop(target: TabId, dropped: TabId, edge: 'left' | 'right' | 'top' | 'bottom'): Promise<void>
	removeFromSplit(id: TabId): Promise<void>
	setSplitDropZone(zone: SplitRect | null): Promise<void>
	setSplitRatios(splitId: string, ratios: number[], length: number): Promise<void>
	saveTab(id: TabId, folderId: string, at: number): Promise<void>
	moveStack(key: string, index: number): Promise<void>
	moveWithinStack(id: TabId, key: string, index: number): Promise<void>
	closeOtherTabs(id: TabId): Promise<void>
	setTabOrganization(organization: TabOrganization): Promise<void>
	setTabDropdowns(on: boolean): Promise<void>
	toggleStack(key: string, anchorX: number): Promise<void>
	openStackForDrag(key: string, anchorX: number): Promise<void>
	closeStack(): Promise<void>
	currentStack(): Promise<StackDropdownState | undefined>
	stackDragPresence(source: DragPresenceSource, inside: boolean): Promise<void>
	openStackMenu(key: string, anchor: MenuAnchor): Promise<void>
	openStripMenu(anchor: MenuAnchor): Promise<void>
	/** Null leaves the caption buttons to Windows. */
	setTitleBarColors(colors: TitleBarColors | null): Promise<void>
	openAppMenu(anchor: MenuAnchor): Promise<void>
	openTabMenu(id: TabId, anchor: MenuAnchor): Promise<void>
	openDefaultAppsSettings(): Promise<void>
	openHistoryMenu(direction: HistoryDirection, anchor: MenuAnchor): Promise<void>
	getPageUi(): Promise<PageUiState>
	setPageUiHeight(height: number): Promise<void>
	getAccountPicker(): Promise<AccountPickerState | null>
	setAccountPickerHeight(height: number): Promise<void>
	closeAccountPicker(): Promise<void>
	onAccountPicker(listener: (state: AccountPickerState | null) => void): Unsubscribe
	getPeek(): Promise<PeekState | null>
	onPeek(listener: (state: PeekState | null) => void): Unsubscribe
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
	getOverview(): Promise<OverviewState | undefined>
	closeOverview(): Promise<void>
	onTabsState(listener: (state: TabsState) => void): Unsubscribe
	onFocusAddress(listener: () => void): Unsubscribe
	onStackDropdown(listener: (state: StackDropdownState | undefined) => void): Unsubscribe
	onNotice(listener: (notice: Notice | undefined) => void): Unsubscribe
	onPageUiState(listener: (state: PageUiState) => void): Unsubscribe
	onLibraryState(listener: (state: LibraryViewState) => void): Unsubscribe
	onOverview(listener: (state: OverviewState | undefined) => void): Unsubscribe
	getSettings(): Promise<Settings>
	setSetting(key: SettingKey, value: unknown): Promise<void>
	onSettingsChanged(listener: (settings: Settings) => void): Unsubscribe
	getShell(): Promise<ShellState>
	openSettings(category?: SettingsCategoryId): Promise<void>
	closeSettings(): Promise<void>
	openToolbox(): Promise<void>
	closeToolbox(): Promise<void>
	openShellMenu(kind: ShellMenuKind, id: string, anchor: MenuAnchor): Promise<void>
	clearBrowsingSession(): Promise<void>
	reportError(report: RendererErrorReport): Promise<void>
	hashText(text: string, algorithm: string): Promise<string>
	hashFile(token: string, algorithm: string): Promise<string>
	hmac(message: string, key: string, algorithm: string): Promise<string>
	diagnose(host: string): Promise<DiagnosticReport>
	previewDns(): Promise<DnsPreview>
	applyDns(token: string): Promise<DnsOutcome>
	undoDns(token: string): Promise<DnsOutcome>
	openLink(input: string, runId: string): Promise<RelayOpenResult>
	allowRelayThirdParties(): Promise<void>
	openRelayedTab(url: string): Promise<void>
	chooseOffer(tool: OfferTool): Promise<OpenPanelOutcome>
	dismissOffer(): Promise<void>
	onRelayAttempt(listener: (event: RelayAttemptEvent) => void): Unsubscribe
	onShellState(listener: (state: ShellState) => void): Unsubscribe
	getToolPanels(): Promise<ToolPanelsState>
	openPanel(content: string, at: Point): Promise<OpenPanelOutcome>
	openPanelWithInput(content: string, input: string): Promise<OpenPanelOutcome>
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
	changePassword(current: string, next: string): Promise<AccountOutcome>
	listEngines(): Promise<EngineInfo[]>
	ensureEngine(id: string): Promise<EnsureOutcome>
	cancelEngine(id: string): Promise<void>
	removeEngine(id: string): Promise<void>
	updateYtDlp(): Promise<YtDlpUpdate>
	onEngineProgress(listener: (progress: EngineProgress) => void): Unsubscribe
	runEngine(engineId: string, panelId: string, job: EngineJobSpec): Promise<string>
	cancelEngineJob(jobId: string): Promise<void>
	savePage(): Promise<PickedFile>
	onEngineOutput(listener: (output: EngineOutput) => void): Unsubscribe
	onEngineExit(listener: (exit: EngineExit) => void): Unsubscribe
	pickFiles(kinds: readonly FileKind[]): Promise<PickedFile[]>
	/** Copies a picked, dropped or made file to the clipboard as a file (see `files:copy-to-clipboard`). */
	copyFileToClipboard(token: string): Promise<void>
	/** Convert's HTML to PDF (see `convert:html-to-pdf`). */
	printHtmlToPdf(token: string, outputName: string): Promise<PickedFile>
	/** Opens a read session for a tool panel (see `session:open`). */
	openToolSession(panelId: string, token: string, kinds: readonly SessionKindName[]): Promise<ViewerOpened>
	/** A results-only session for a panel (see `session:open-save`). */
	openSaveSession(panelId: string, kinds: readonly SessionKindName[]): Promise<ViewerOpened>
	/** The background remover's session (see `bgremove:open`). */
	openBackgroundPicture(panelId: string, token: string): Promise<ViewerOpened>
	/** Hand over the `File`s of a drop (`event.dataTransfer.files`); resolves with a token for each. */
	openDropped(files: readonly File[]): Promise<PickedFile[]>
	openViewerFile(token: string, panelId: string): Promise<ViewerOpened>
	closeViewerFile(id: string): Promise<void>
	onAccountState(listener: (state: AccountState) => void): Unsubscribe
	getVaultState(): Promise<VaultState>
	vaultSummaries(): Promise<LoginSummary[]>
	revealPassword(id: string): Promise<string>
	revealOldPassword(id: string, index: number): Promise<string>
	saveLogin(login: NewLogin): Promise<string>
	updateLogin(id: string, edit: LoginEdit): Promise<void>
	deleteLogin(id: string): Promise<void>
	restoreLogin(id: string): Promise<void>
	loginHistory(id: string): Promise<PasswordHistoryEntry[]>
	copyPassword(id: string): Promise<void>
	importPasswordsCsv(token: string): Promise<VaultImportResult>
	exportPasswordsCsv(): Promise<PickedFile>
	neverSaves(): Promise<NeverSave[]>
	removeNeverSave(origin: string): Promise<void>
	onVaultState(listener: (state: VaultState) => void): Unsubscribe
	fillLogin(pageId: TabId, id: string): Promise<FillOutcome>
	answerSaveLogin(pageId: TabId, answer: SaveAnswer): Promise<SaveOutcome>
	getSyncState(): Promise<SyncState>
	signInToSync(): Promise<SyncState>
	signOutOfSync(): Promise<SyncState>
	turnOffSync(): Promise<SyncState>
	cancelSyncSignIn(): Promise<void>
	turnOnSync(password: string): Promise<RecoveryKeyShown>
	syncServerHasKeys(): Promise<boolean>
	joinSync(current: string, password: string): Promise<SyncState>
	confirmRecoverySaved(): Promise<SyncState>
	showRecoveryKey(): Promise<RecoveryKeyShown>
	uploadLocalCopy(): Promise<SyncState>
	onSyncState(listener: (state: SyncState) => void): Unsubscribe
	signInToRestore(): Promise<SyncState>
	restoreWithPassword(password: string): Promise<AccountOutcome>
	restoreWithRecoveryKey(key: string): Promise<void>
	setPasswordAfterRecovery(password: string): Promise<AccountOutcome>
	getUpdateState(): Promise<UpdateState>
	checkForUpdates(): Promise<UpdateState>
	relaunchToUpdate(): Promise<void>
	setUpdatePillSize(width: number, height: number): Promise<void>
	onUpdateState(listener: (state: UpdateState) => void): Unsubscribe
	getDefaultBrowserState(): Promise<DefaultBrowserState>
	dismissDefaultBrowserOffer(): Promise<void>
	getCredits(): Promise<string>
	importSources(): Promise<ImportSourceView[]>
	importBookmarks(sourceId: string): Promise<BookmarkImportOutcome>
	importBookmarksFile(token: string): Promise<BookmarkImportOutcome>
	watchPasswords(sourceId: string): Promise<void>
	cancelPasswordWatch(): Promise<void>
	recycleFile(token: string): Promise<void>
	takePendingImport(): Promise<{ action: 'passwords'; sourceId: string } | null>
	onPasswordExport(listener: (file: { token: string; name: string }) => void): Unsubscribe
	onPasswordWatchTimedOut(listener: () => void): Unsubscribe
}
