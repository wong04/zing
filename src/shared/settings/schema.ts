import { VERTICAL_TABS } from '../layout/chrome'
import { DEFAULT_SLEEPING_TABS, isSleepSettings } from '../session/sleepSettings'

/**
 * Every persisted preference, with an explicit default, in one table. Port of
 * Krackn's AppSettings.swift. A missing key always reads as its default here,
 * never as a zero value: Krackn's `bool(forKey:)` returned false for unset
 * keys and turned "never overwrite files" into "always overwrite".
 */

interface Setting<T> {
	readonly default: T
	readonly accepts: (value: unknown) => value is T
}

function oneOf<const T extends string>(values: readonly T[], defaultValue: T): Setting<T> {
	return {
		default: defaultValue,
		accepts: (v): v is T => typeof v === 'string' && (values as readonly string[]).includes(v),
	}
}

function flag(defaultValue: boolean): Setting<boolean> {
	return { default: defaultValue, accepts: (v): v is boolean => typeof v === 'boolean' }
}

function numberIn(min: number, max: number, defaultValue: number): Setting<number> {
	return {
		default: defaultValue,
		accepts: (v): v is number => typeof v === 'number' && Number.isFinite(v) && v >= min && v <= max,
	}
}

function integerIn(min: number, max: number, defaultValue: number): Setting<number> {
	return {
		default: defaultValue,
		accepts: (v): v is number => Number.isInteger(v) && (v as number) >= min && (v as number) <= max,
	}
}

function text(defaultValue: string): Setting<string> {
	return { default: defaultValue, accepts: (v): v is string => typeof v === 'string' }
}

function hexColor(defaultValue: string): Setting<string> {
	return { default: defaultValue, accepts: (v): v is string => typeof v === 'string' && /^#[0-9a-fA-F]{6}$/.test(v) }
}

function textList(): Setting<readonly string[]> {
	return {
		default: [],
		accepts: (v): v is readonly string[] => Array.isArray(v) && v.every((s) => typeof s === 'string'),
	}
}

export interface StartShortcut {
	readonly title: string
	readonly url: string
}

/** Null means "never edited": the new-tab page shows the built-in tiles. */
function startShortcuts(): Setting<readonly StartShortcut[] | null> {
	const isShortcut = (s: unknown): s is StartShortcut =>
		typeof s === 'object' &&
		s !== null &&
		typeof (s as StartShortcut).title === 'string' &&
		typeof (s as StartShortcut).url === 'string'
	return {
		default: null,
		accepts: (v): v is readonly StartShortcut[] | null => v === null || (Array.isArray(v) && v.every(isShortcut)),
	}
}

export const APPEARANCES = ['system', 'light', 'dark'] as const
export const ACCENTS = ['yellow', 'green', 'blue', 'magenta', 'purple', 'red', 'mono', 'midnight', 'custom'] as const
/** The seed for the `custom` accent until the user picks one: the reference beige, as the seed; the generated palette is greyer than Amber. */
export const DEFAULT_CUSTOM_COLOR = '#D9B872'
export const SEARCH_ENGINES = ['google', 'duckduckgo', 'bing', 'brave', 'kagi', 'startpage'] as const
export const DOH_PROVIDERS = ['cloudflare', 'cloudflare-security', 'google', 'quad9'] as const
export const TAB_ORGANIZATIONS = ['manual', 'domain', 'alphabetical', 'recentlyUsed', 'newest'] as const
export const AUTO_CLOSE_RULES = ['hour', 'day', 'week', 'custom'] as const
export const TAB_BAR_POSITIONS = ['top', 'left', 'right'] as const
export const TAB_COLOURS = ['colour', 'none'] as const

export const SETTINGS_SCHEMA = {
	/** Krackn's `appTheme`: Auto / Light / Dark. Every view is forced to it by the host (`HostedView.FollowTheme`). */
	theme: oneOf(APPEARANCES, 'system'),
	accent: oneOf(ACCENTS, 'yellow'),
	/** Zing: the seed colour the `custom` accent is generated from, as #RRGGBB. */
	customColor: hexColor(DEFAULT_CUSTOM_COLOR),
	/** Empty means the local new-tab page, the only homepage that makes no network request. */
	homepage: text(''),
	startShortcuts: startShortcuts(),
	searchEngine: oneOf(SEARCH_ENGINES, 'google'),
	blockTrackers: flag(true),
	defaultQuality: integerIn(10, 100, 80),
	preserveOriginals: flag(true),
	dohProvider: oneOf(DOH_PROVIDERS, 'cloudflare'),
	tabOrganization: oneOf(TAB_ORGANIZATIONS, 'manual'),
	autoCloseEnabled: flag(false),
	autoCloseRule: oneOf(AUTO_CLOSE_RULES, 'day'),
	autoCloseCustomHours: numberIn(0.25, 24 * 365, 12),
	autoCloseAtTimeEnabled: flag(false),
	autoCloseHour: integerIn(0, 23, 3),
	autoCloseMinute: integerIn(0, 59, 0),
	/** Trades the keeps-nothing guarantee for a profile on disk; flips the privacy banner. */
	persistentProfile: flag(false),
	defaultUserAgent: flag(false),
	acceleratedCanvas: flag(true),
	/** Off by default: remembering open tabs means writing visited URLs to disk. */
	restoreTabs: flag(false),
	securityRealtime: flag(true),
	securityDownloadScan: flag(true),
	securityDomainBlocking: flag(true),
	securitySignatureCheck: flag(true),
	sidebarPinned: flag(true),
	/** Feature names only, never what was done with them. */
	recentFeatures: textList(),
	showFavouritesBar: flag(true),
	/** The empty favourites bar's "Press Ctrl+D" hint, until its close button is clicked. */
	favouritesHint: flag(true),
	passwordAutofill: flag(true),
	tabDropdowns: flag(false),
	/** Zing: where the tabs sit. On the side by default, as in Arc and Dia (docs/phase-4.md). */
	tabBarPosition: oneOf(TAB_BAR_POSITIONS, 'left'),
	/** Zing: tab cards and favourites in the theme's colour, or neutral. */
	tabColours: oneOf(TAB_COLOURS, 'colour'),
	/** Zing: the side rail's width in DIPs; dragging its edge sets it (clampRailWidth). */
	verticalTabWidth: integerIn(VERTICAL_TABS.minWidth, VERTICAL_TABS.maxWidth, VERTICAL_TABS.defaultWidth),
	/** Zing: the side rail as a column of icons; on the left the full rail slides out while the pointer is over it. */
	verticalTabsCollapsed: flag(false),
	/** Zing: discard idle tabs' renderers to save memory; the record and URL stay. */
	sleepingTabs: { default: DEFAULT_SLEEPING_TABS, accepts: isSleepSettings },
} as const

type Schema = typeof SETTINGS_SCHEMA

export type SettingKey = keyof Schema

export type Settings = { readonly [K in SettingKey]: Schema[K]['default'] }

export const SETTING_KEYS = Object.keys(SETTINGS_SCHEMA) as SettingKey[]

export const DEFAULT_SETTINGS: Settings = Object.freeze(
	Object.fromEntries(SETTING_KEYS.map((k) => [k, SETTINGS_SCHEMA[k].default])) as unknown as Settings,
)

/**
 * Keys Zing once had and dropped. A stored file may still hold one (it was written while the key
 * existed), so reading skips it instead of failing the whole file as unknown; the next save leaves it out.
 * confirmQuit: Krackn's "Confirm before quitting" switch, which Krackn never read either.
 */
export const RETIRED_SETTING_KEYS: readonly string[] = ['confirmQuit']

export function isSettingKey(key: string): key is SettingKey {
	return Object.hasOwn(SETTINGS_SCHEMA, key)
}

export function acceptsValue<K extends SettingKey>(key: K, value: unknown): value is Settings[K] {
	return SETTINGS_SCHEMA[key].accepts(value)
}
