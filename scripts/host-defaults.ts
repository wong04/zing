// The defaults the C# host needs that are defined in TypeScript: settings,
// the empty library and the command list. Exported as JSON so there's one
// source of truth. `src/shared/hostDefaults.test.ts` fails if the committed
// files drift; regenerate with `npm run gen:host-defaults`.
import { SEARCH_URLS } from '../src/shared/browser/searchEngines'
import { COMMANDS, MENU_LAYOUT } from '../src/shared/commands/registry'
import { shortcutLabel } from '../src/shared/commands/shortcut'
import { emptyLibrary } from '../src/shared/library/library'
import { nativeTypes, viewableLists } from '../src/shared/convert/viewable'
import { tabOrganizationLabel, tabOrganizations } from '../src/shared/browser/tabOrganizer'
import { CHROME_LAYOUT, LIBRARY_PANEL_WIDTH } from '../src/shared/ipc'
import { VERTICAL_TABS } from '../src/shared/layout/chrome'
import {
	ACCENTS,
	APPEARANCES,
	AUTO_CLOSE_RULES,
	DOH_PROVIDERS,
	DEFAULT_SETTINGS,
	RETIRED_SETTING_KEYS,
	SEARCH_ENGINES,
	TAB_BAR_POSITIONS,
	TAB_COLOURS,
	TAB_ORGANIZATIONS,
	type SettingKey,
} from '../src/shared/settings/schema'
import { MAX_SLEEP_MINUTES, MIN_SLEEP_MINUTES } from '../src/shared/session/sleepSettings'
import { SIDEBAR_COLUMN_WIDTH } from '../src/shared/shell/shellState'
import { autofillBridgeName, autofillFillFunction, autofillPickerEnterFlag, detectionScript } from '../src/shared/vault/autofillScript'

export type SettingRule =
	| { readonly type: 'enum'; readonly values: readonly string[] }
	| { readonly type: 'flag' }
	| { readonly type: 'number' | 'integer'; readonly min: number; readonly max: number }
	| { readonly type: 'text' | 'textList' | 'startShortcuts' | 'linkList' }
	| { readonly type: 'hexColor' }
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
	customColor: { type: 'hexColor' },
	homepage: text,
	startShortcuts: { type: 'startShortcuts' },
	searchEngine: oneOf(SEARCH_ENGINES),
	blockTrackers: flag,
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
	sidebarPinned: flag,
	recentFeatures: { type: 'textList' },
	showFavouritesBar: flag,
	favouritesHint: flag,
	passwordAutofill: flag,
	tabDropdowns: flag,
	linkPreviews: flag,
	linkPreviewsShiftClick: flag,
	linkPreviewsFromNewWindows: flag,
	tabBarPosition: oneOf(TAB_BAR_POSITIONS),
	tabColours: oneOf(TAB_COLOURS),
	verticalTabWidth: range('integer', VERTICAL_TABS.minWidth, VERTICAL_TABS.maxWidth),
	verticalTabsCollapsed: flag,
	sleepingTabs: { type: 'sleep', min: MIN_SLEEP_MINUTES, max: MAX_SLEEP_MINUTES },
	toolLinks: { type: 'linkList' },
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
		case 'hexColor':
			return typeof value === 'string' && /^#[0-9a-fA-F]{6}$/.test(value)
		case 'textList':
			return Array.isArray(value) && value.every((s) => typeof s === 'string')
		case 'startShortcuts':
			return (
				value === null ||
				(Array.isArray(value) && value.every((s) => isPlainObject(s) && typeof s.title === 'string' && typeof s.url === 'string'))
			)
		case 'linkList':
			return Array.isArray(value) && value.every((s) => isPlainObject(s) && typeof s.title === 'string' && typeof s.url === 'string')
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
		'settings-retired.json': RETIRED_SETTING_KEYS,
		'library.json': stableLibrary,
		'commands.json': COMMANDS.map(({ id, label, shortcuts }) => ({
			id,
			label,
			shortcuts: shortcuts.map((s) => ({ code: s.code, ctrl: Boolean(s.ctrl), shift: Boolean(s.shift), alt: Boolean(s.alt), label: shortcutLabel(s) })),
		})),
		'menu.json': MENU_LAYOUT,
		'tab-organizations.json': tabOrganizations.map((id) => ({ id, label: tabOrganizationLabel[id] })),
		'search-engines.json': SEARCH_URLS,
		'viewable.json': { native: nativeTypes, ...viewableLists },
		'autofill.json': { detectionScript, messageKey: autofillBridgeName, fillFunction: autofillFillFunction, pickerEnterFlag: autofillPickerEnterFlag },
		'layout.json': { ...CHROME_LAYOUT, sidebarColumnWidth: SIDEBAR_COLUMN_WIDTH, libraryPanelWidth: LIBRARY_PANEL_WIDTH, verticalTabs: VERTICAL_TABS },
	}
}
