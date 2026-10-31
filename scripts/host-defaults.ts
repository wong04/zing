// The defaults the C# host needs that are defined in TypeScript: settings,
// the empty library and the command list. Exported as JSON so there's one
// source of truth. `src/shared/hostDefaults.test.ts` fails if the committed
// files drift; regenerate with `npm run gen:host-defaults`.
import { SEARCH_URLS } from '../src/shared/browser/searchEngines'
import { COMMANDS } from '../src/shared/commands/registry'
import { emptyLibrary } from '../src/shared/library/library'
import { CHROME_LAYOUT } from '../src/shared/ipc'
import {
	ACCENTS,
	APPEARANCES,
	AUTO_CLOSE_RULES,
	DOH_PROVIDERS,
	DEFAULT_SETTINGS,
	SEARCH_ENGINES,
	TAB_BAR_POSITIONS,
	TAB_ORGANIZATIONS,
	type SettingKey,
} from '../src/shared/settings/schema'
import { MAX_SLEEP_MINUTES, MIN_SLEEP_MINUTES } from '../src/shared/session/sleepSettings'
import { SIDEBAR_COLUMN_WIDTH } from '../src/shared/shell/shellState'

export type SettingRule =
	| { readonly type: 'enum'; readonly values: readonly string[] }
	| { readonly type: 'flag' }
	| { readonly type: 'number' | 'integer'; readonly min: number; readonly max: number }
	| { readonly type: 'text' | 'textList' | 'startShortcuts' }
	| { readonly type: 'sleep'; readonly min: number; readonly max: number }

const oneOf = (values: readonly string[]): SettingRule => ({ type: 'enum', values })
const flag: SettingRule = { type: 'flag' }
const text: SettingRule = { type: 'text' }
const range = (type: 'number' | 'integer', min: number, max: number): SettingRule => ({ type, min, max })

/**
 * How the C# host validates each setting. The schema's validators are
 * closures, so the rules are restated here; `hostDefaults.test.ts` probes
 * every key with boundary values and fails if a rule and its validator disagree.
 */
export const SETTING_RULES: Record<SettingKey, SettingRule> = {
	theme: oneOf(APPEARANCES),
	accent: oneOf(ACCENTS),
	homepage: text,
	startShortcuts: { type: 'startShortcuts' },
	searchEngine: oneOf(SEARCH_ENGINES),
	blockTrackers: flag,
	confirmQuit: flag,
	defaultQuality: range('integer', 10, 100),
	preserveOriginals: flag,
	dohProvider: oneOf(DOH_PROVIDERS),
	tabOrganization: oneOf(TAB_ORGANIZATIONS),
	autoCloseEnabled: flag,
	autoCloseRule: oneOf(AUTO_CLOSE_RULES),
	autoCloseCustomHours: range('number', 0.25, 24 * 365),
	autoCloseAtTimeEnabled: flag,
	autoCloseHour: range('integer', 0, 23),
	autoCloseMinute: range('integer', 0, 59),
	persistentProfile: flag,
	defaultUserAgent: flag,
	acceleratedCanvas: flag,
	restoreTabs: flag,
	securityRealtime: flag,
	securityDownloadScan: flag,
	securityDomainBlocking: flag,
	securitySignatureCheck: flag,
	sidebarPinned: flag,
	recentFeatures: { type: 'textList' },
	showFavouritesBar: flag,
	passwordAutofill: flag,
	tabDropdowns: flag,
	tabBarPosition: oneOf(TAB_BAR_POSITIONS),
	sleepingTabs: { type: 'sleep', min: MIN_SLEEP_MINUTES, max: MAX_SLEEP_MINUTES },
}

const isPlainObject = (v: unknown): v is Record<string, unknown> => typeof v === 'object' && v !== null && !Array.isArray(v)
const isFiniteIn = (v: unknown, min: number, max: number): v is number =>
	typeof v === 'number' && Number.isFinite(v) && v >= min && v <= max

/** Reference interpreter for a rule: the C# host implements exactly this. */
export function ruleAccepts(rule: SettingRule, value: unknown): boolean {
	switch (rule.type) {
		case 'enum':
			return typeof value === 'string' && rule.values.includes(value)
		case 'flag':
			return typeof value === 'boolean'
		case 'number':
			return isFiniteIn(value, rule.min, rule.max)
		case 'integer':
			return Number.isInteger(value) && isFiniteIn(value, rule.min, rule.max)
		case 'text':
			return typeof value === 'string'
		case 'textList':
			return Array.isArray(value) && value.every((s) => typeof s === 'string')
		case 'startShortcuts':
			return (
				value === null ||
				(Array.isArray(value) && value.every((s) => isPlainObject(s) && typeof s.title === 'string' && typeof s.url === 'string'))
			)
		case 'sleep': {
			if (!isPlainObject(value) || Object.keys(value).length !== 3) return false
			return typeof value.enabled === 'boolean' && typeof value.sleepPinned === 'boolean' && isFiniteIn(value.afterMinutes, rule.min, rule.max)
		}
	}
}

export function hostDefaults(): Record<string, unknown> {
	// The Favourites folder's updatedAt is the only time-dependent field; pin it so the file is stable.
	const library = emptyLibrary()
	const stableLibrary = { ...library, bookmarks: library.bookmarks.map((node) => ({ ...node, updatedAt: 0 })) }
	return {
		'settings.json': DEFAULT_SETTINGS,
		'settings-schema.json': SETTING_RULES,
		'library.json': stableLibrary,
		'commands.json': COMMANDS.map(({ id, label }) => ({ id, label })),
		'search-engines.json': SEARCH_URLS,
		'layout.json': { ...CHROME_LAYOUT, sidebarColumnWidth: SIDEBAR_COLUMN_WIDTH },
	}
}
