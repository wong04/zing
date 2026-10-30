// What the sidebar, settings sheet, tool panels and account gate say to each
// other across the IPC bridge. Pure types and constants: main owns the state,
// the chrome and surface renderers draw it.
import type { Rect } from './panelLayout'

/** The app sidebar's pane width and the gap around it, in DIPs (Krackn Sidebar.width / inset). */
export const SIDEBAR = { width: 196, inset: 8 } as const

/**
 * The column a pinned sidebar takes from the chrome. A whole number of DIPs,
 * so the page view's left edge lands on a device pixel at every scale factor.
 */
export const SIDEBAR_COLUMN_WIDTH = SIDEBAR.width + SIDEBAR.inset

export const MAX_VISIBLE_PANELS = 3

export interface Point {
	readonly x: number
	readonly y: number
}

export const SETTINGS_CATEGORIES = [
	'general',
	'browsing',
	'newTab',
	'tabs',
	'files',
	'privacy',
	'security',
	'advanced',
	'account',
	'about',
] as const
export type SettingsCategoryId = (typeof SETTINGS_CATEGORIES)[number]

export function isSettingsCategory(value: unknown): value is SettingsCategoryId {
	return (SETTINGS_CATEGORIES as readonly unknown[]).includes(value)
}

/** The surface view's state. `origin` is where that view sits in window content coordinates. */
export interface ShellState {
	readonly settingsOpen: boolean
	readonly settingsCategory: SettingsCategoryId
	readonly sidebarRevealed: boolean
	/** True while the account gate covers the window. */
	readonly gated: boolean
	readonly origin: Point
	readonly appVersion: string
}

export interface ToolPanelView {
	readonly id: string
	/** A `panelId` string such as "tool:Hashes". */
	readonly content: string
	readonly title: string
	/** Window content coordinates. */
	readonly frame: Rect
	readonly minimized: boolean
}

export interface ToolPanelsState {
	readonly panels: readonly ToolPanelView[]
	readonly frontId: string | undefined
	readonly dragging: boolean
}

export const EMPTY_TOOL_PANELS: ToolPanelsState = { panels: [], frontId: undefined, dragging: false }

export type OpenPanelOutcome =
	| { readonly status: 'opened' | 'focused' | 'restored' }
	/** Three are already showing; minimize or close one first. */
	| { readonly status: 'full' }

export type ShellMenuKind = 'recent' | 'minimized'

export interface AccountProfile {
	readonly username: string
	readonly displayName: string
	readonly email: string
}

export interface AccountState {
	readonly hasAccount: boolean
	readonly signedIn: boolean
	/** The stored password check is unreadable (new machine, lost key): choose a password again. */
	readonly needsPasswordReset: boolean
	readonly requirePasswordOnLaunch: boolean
	readonly profile: AccountProfile | null
}

export const ACCOUNT_ERROR_CODES = [
	'account-exists',
	'weak-password',
	'empty-field',
	'wrong-password',
	'no-account',
	'encryption-unavailable',
	'reset-not-needed',
] as const
export type AccountErrorCode = (typeof ACCOUNT_ERROR_CODES)[number]

export const MIN_PASSWORD_LENGTH = 8

export type AccountOutcome =
	| { readonly status: 'ok'; readonly state: AccountState }
	| { readonly status: 'rejected'; readonly code: AccountErrorCode; readonly message: string }
