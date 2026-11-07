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

export type PermissionKind = 'camera' | 'microphone' | 'camera and microphone' | 'location' | 'notifications'

export interface PermissionPrompt {
	readonly id: number
	readonly origin: string
	readonly kind: PermissionKind
}

export interface PageUiState {
	readonly find: FindState | undefined
	/** Bumped each time Find is invoked so an open bar re-takes focus. */
	readonly findFocus: number
	readonly downloads: readonly DownloadEntry[]
	readonly downloadsOpen: boolean
	readonly permission: PermissionPrompt | undefined
}

export const EMPTY_PAGE_UI: PageUiState = {
	find: undefined,
	findFocus: 0,
	downloads: [],
	downloadsOpen: false,
	permission: undefined,
}

export function hasVisiblePageUi(state: PageUiState): boolean {
	return state.find !== undefined || state.permission !== undefined || (state.downloadsOpen && state.downloads.length > 0)
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
	readonly rect: { readonly x: number; readonly y: number; readonly width: number; readonly height: number }
	/** Newest first. Usernames only, never a password. */
	readonly entries: readonly AccountPickerEntry[]
}

export type FillOutcome = 'filled' | 'not-filled' | 'cancelled' | 'refused'
