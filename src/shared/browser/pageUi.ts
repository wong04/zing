/** State of the panels view: the find bar, downloads list and permission prompt drawn over the page. */

export interface FindState {
	readonly query: string
	/** 1-based index of the highlighted match; 0 when there is none. */
	readonly active: number
	readonly matches: number
}

export type DownloadStatus = 'progressing' | 'completed' | 'cancelled' | 'failed'

export interface DownloadEntry {
	readonly id: number
	readonly filename: string
	readonly status: DownloadStatus
	readonly received: number
	/** 0 when the server sent no length. */
	readonly total: number
	readonly error?: string
}

export type PermissionKind = 'camera' | 'microphone' | 'camera and microphone' | 'location' | 'notifications' | 'clipboard' | 'multiple downloads' | 'files' | 'pop-ups'

export interface PermissionPrompt {
	readonly id: number
	readonly origin: string
	readonly kind: PermissionKind
	/** The asking tab's title when it isn't the tab on screen. */
	readonly tab?: string
}

export type ScriptDialogKind = 'alert' | 'confirm' | 'prompt' | 'beforeunload'

/** A page's JavaScript dialog, drawn by Zing rather than the engine: one at a time, the oldest first. */
export interface ScriptDialogPrompt {
	readonly id: number
	readonly kind: ScriptDialogKind
	/** The asking page's origin; 'null' when it has no web origin. */
	readonly origin: string
	readonly message: string
	/** A prompt's starting text; absent for the other kinds. */
	readonly defaultText?: string
	/** The asking tab's title when it isn't the tab on screen. */
	readonly tab?: string
}

/** The vault's offer to save a sign-in, or to update a saved one. Names the origin and username only; the host holds the password. */
export interface SaveLoginPrompt {
	/** The tab it is for. */
	readonly pageId: string
	readonly kind: 'save' | 'update'
	readonly origin: string
	readonly username: string
	/** For an update, the saved login's username whose password it replaces. */
	readonly replaces?: string
}

export type SaveAnswer = 'save' | 'not-now' | 'never'

/** `cancelled`: Windows Hello was cancelled, so nothing was stored. `unavailable`: Hello can't be used, nothing was stored and the host told the person why. `gone`: the prompt had already closed. */
export type SaveOutcome = 'saved' | 'updated' | 'dismissed' | 'cancelled' | 'unavailable' | 'gone'

export interface PageUiState {
	readonly find: FindState | undefined
	/** Bumped each time Find is invoked so an open bar re-takes focus. */
	readonly findFocus: number
	readonly downloads: readonly DownloadEntry[]
	readonly downloadsOpen: boolean
	readonly permission: PermissionPrompt | undefined
	readonly saveLogin?: SaveLoginPrompt | undefined
	readonly dialog?: ScriptDialogPrompt | undefined
}

export const EMPTY_PAGE_UI: PageUiState = {
	find: undefined,
	findFocus: 0,
	downloads: [],
	downloadsOpen: false,
	permission: undefined,
	saveLogin: undefined,
	dialog: undefined,
}

export function hasVisiblePageUi(state: PageUiState): boolean {
	return state.find !== undefined || state.permission !== undefined || state.dialog !== undefined || state.saveLogin !== undefined || (state.downloadsOpen && state.downloads.length > 0)
}

export type HistoryDirection = 'back' | 'forward'
export type FindDirection = 'next' | 'previous'

export interface AccountPickerEntry {
	readonly id: string
	readonly username: string
}

/** The vault's account picker, drawn by Zing under the focused sign-in field; the page never sees it. */
export interface AccountPickerState {
	/** The tab whose field it is for. */
	readonly pageId: string
	/** The field, in DIPs of the window's content. */
	readonly rect: PickerRect
	/** The page's own bounds, in the same DIPs: the picker is kept inside them. */
	readonly viewport: PickerRect
	/** Newest first. Usernames only, never a password. */
	readonly entries: readonly AccountPickerEntry[]
	/** The entry the arrow keys selected, -1 for none: it opens with nothing selected. */
	readonly active: number
}

type PickerRect = { readonly x: number; readonly y: number; readonly width: number; readonly height: number }

/** `ignored`: a click on a picker that hadn't been on screen, where it is, for 500 ms yet. */
export type FillOutcome = 'filled' | 'not-filled' | 'cancelled' | 'refused' | 'ignored'
