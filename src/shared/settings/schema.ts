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
export const ACCENTS = ['yellow', 'green', 'blue', 'magenta', 'purple', 'red', 'mono'] as const
export const SEARCH_ENGINES = ['google', 'duckduckgo', 'bing', 'brave', 'kagi', 'startpage'] as const
export const DOH_PROVIDERS = ['cloudflare', 'cloudflare-security', 'google', 'quad9'] as const
export const TAB_ORGANIZATIONS = ['manual', 'domain', 'alphabetical', 'recentlyUsed', 'newest'] as const
export const AUTO_CLOSE_RULES = ['hour', 'day', 'week', 'custom'] as const
export const TAB_BAR_POSITIONS = ['top', 'left', 'right'] as const

export const SETTINGS_SCHEMA = {
	/** Krackn's `appTheme`: Auto / Light / Dark. Drives `nativeTheme.themeSource`. */
	theme: oneOf(APPEARANCES, 'system'),
	accent: oneOf(ACCENTS, 'yellow'),
	/** Empty means the local new-tab page, the only homepage that makes no network request. */
	homepage: text(''),
	startShortcuts: startShortcuts(),
	searchEngine: oneOf(SEARCH_ENGINES, 'google'),
	blockTrackers: flag(true),
	confirmQuit: flag(false),
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
	passwordAutofill: flag(true),
	tabDropdowns: flag(false),
	/** Zing: where the tab strip sits. */
	tabBarPosition: oneOf(TAB_BAR_POSITIONS, 'top'),
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

export function isSettingKey(key: string): key is SettingKey {
	return Object.hasOwn(SETTINGS_SCHEMA, key)
}

export function acceptsValue<K extends SettingKey>(key: K, value: unknown): value is Settings[K] {
	return SETTINGS_SCHEMA[key].accepts(value)
}
