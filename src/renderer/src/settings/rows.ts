// The Settings sheet as data (Krackn SettingsView.swift panes). Every key in
// SETTINGS_SCHEMA must appear here: a setting with no control is the bug
// Krackn's AppSettings exists to prevent, and rows.test.ts enforces it.
import { VERTICAL_TABS } from '@shared/layout/chrome'
import { tabOrganizationDetail, tabOrganizationLabel } from '@shared/browser/tabOrganizer'
import { TRACKER_DOMAINS } from '@shared/browser/trackerRules'
import {
	AUTO_CLOSE_RULES,
	DOH_PROVIDERS,
	SEARCH_ENGINES,
	TAB_BAR_POSITIONS,
	TAB_COLOURS,
	TAB_ORGANIZATIONS,
	type SettingKey,
	type Settings,
} from '@shared/settings'
import type { SettingsCategoryId } from '@shared/shell/shellState'

export interface Option {
	readonly value: string | number | boolean
	readonly label: string
}

export type RowControl =
	| { readonly kind: 'toggle' }
	| { readonly kind: 'segmented'; readonly options: readonly Option[]; readonly width: number }
	| { readonly kind: 'menu'; readonly options: readonly Option[] }
	| { readonly kind: 'text'; readonly placeholder: string; readonly width: number }
	| { readonly kind: 'slider'; readonly min: number; readonly max: number; readonly step: number; readonly unit: string }

export interface SettingRow {
	readonly type: 'setting'
	readonly key: SettingKey
	readonly title: string
	readonly detail?: string | ((s: Settings) => string | undefined)
	readonly warning?: (s: Settings) => boolean
	readonly visible?: (s: Settings) => boolean
	/** Shown but not editable; only toggles honour it. */
	readonly disabled?: (s: Settings) => boolean
	readonly control: RowControl
}

export type CustomId =
	| 'accent'
	| 'default-browser'
	| 'shortcuts'
	| 'tool-links'
	| 'engines'
	| 'sleeping-tabs'
	| 'vault'
	| 'privacy-banner'
	| 'clear-session'
	| 'recent-tools'
	| 'account'
	| 'about'
	| 'import'

export interface CustomRow {
	readonly type: 'custom'
	readonly id: CustomId
	/** Settings this row edits, so the coverage test sees them. */
	readonly keys: readonly SettingKey[]
	readonly visible?: (s: Settings) => boolean
}

export type PaneItem = SettingRow | CustomRow

export interface Category {
	readonly id: SettingsCategoryId
	readonly title: string
}

export const CATEGORIES: readonly Category[] = [
	{ id: 'general', title: 'General' },
	{ id: 'browsing', title: 'Browsing' },
	{ id: 'newTab', title: 'New Tab' },
	{ id: 'tabs', title: 'Tabs' },
	{ id: 'files', title: 'Files' },
	{ id: 'privacy', title: 'Privacy' },
	{ id: 'security', title: 'Security' },
	{ id: 'advanced', title: 'Advanced' },
	{ id: 'account', title: 'Account' },
	{ id: 'about', title: 'About' },
	{ id: 'import', title: 'Import' },
]

const ENGINE_LABELS: Readonly<Record<(typeof SEARCH_ENGINES)[number], string>> = {
	google: 'Google',
	duckduckgo: 'DuckDuckGo (private)',
	bing: 'Bing',
	brave: 'Brave Search (private)',
	kagi: 'Kagi (private)',
	startpage: 'Startpage (private)',
}

export const DOH_LABELS: Readonly<Record<(typeof DOH_PROVIDERS)[number], string>> = {
	cloudflare: 'Cloudflare',
	'cloudflare-security': 'Cloudflare (malware blocking)',
	google: 'Google',
	quad9: 'Quad9',
}

const RULE_LABELS: Readonly<Record<(typeof AUTO_CLOSE_RULES)[number], string>> = {
	hour: '1 hour',
	day: '1 day',
	week: '1 week',
	custom: 'Custom',
}

const TAB_COLOUR_LABELS: Readonly<Record<(typeof TAB_COLOURS)[number], string>> = { colour: 'One colour', none: 'None' }
const POSITION_LABELS: Readonly<Record<(typeof TAB_BAR_POSITIONS)[number], string>> = {
	top: 'Top',
	left: 'Left',
	right: 'Right',
}

const pad = (n: number): string => String(n).padStart(2, '0')

function options<T extends string>(values: readonly T[], labels: Readonly<Record<T, string>>): Option[] {
	return values.map((value) => ({ value, label: labels[value] }))
}

const toggle = (key: SettingKey, title: string, extra: Partial<Omit<SettingRow, 'type' | 'key' | 'title' | 'control'>> = {}): SettingRow => ({
	type: 'setting',
	key,
	title,
	control: { kind: 'toggle' },
	...extra,
})

// Phase 8: About draws this beside Check now (CustomRows), so it is bound here rather than as a pane entry.
export const CHECK_FOR_UPDATES_ROW: SettingRow = toggle('checkForUpdates', 'Check for updates', {
	detail: 'Asks GitHub for the latest version every few hours. Sends nothing about you.',
})

export const PANES: Readonly<Record<SettingsCategoryId, readonly PaneItem[]>> = {
	general: [
		{
			type: 'setting',
			key: 'theme',
			title: 'Appearance',
			control: {
				kind: 'segmented',
				width: 220,
				options: [
					{ value: 'system', label: 'Auto' },
					{ value: 'light', label: 'Light' },
					{ value: 'dark', label: 'Dark' },
				],
			},
		},
		{ type: 'custom', id: 'accent', keys: ['accent', 'customColor'] },
		{
			type: 'setting',
			key: 'tabColours',
			title: 'Tab colours',
			detail: 'For tabs and favourites.',
			control: { kind: 'segmented', width: 200, options: options(TAB_COLOURS, TAB_COLOUR_LABELS) },
		},
		{ type: 'custom', id: 'default-browser', keys: [] },
		toggle('sidebarPinned', 'Keep the sidebar open', {
			detail: (s) => (s.sidebarPinned ? undefined : 'Move to the left edge to show it. Ctrl+S pins it again.'),
		}),
	],
	browsing: [
		{
			type: 'setting',
			key: 'searchEngine',
			title: 'Search engine',
			control: { kind: 'menu', options: options(SEARCH_ENGINES, ENGINE_LABELS) },
		},
		{
			type: 'setting',
			key: 'homepage',
			title: 'Homepage',
			detail: (s) => (s.homepage === '' ? 'The built-in new-tab page.' : undefined),
			control: { kind: 'text', placeholder: 'New-tab page', width: 260 },
		},
		toggle('showFavouritesBar', 'Show the favourites bar'),
		toggle('favouritesHint', 'Show the Ctrl+D hint on an empty favourites bar'),
		toggle('passwordAutofill', 'Offer to fill saved passwords', {
			detail: 'A key appears in sign-in fields. Passwords are kept in the vault on this PC, and with sync on, an encrypted copy is kept on the sync server.',
		}),
		{ type: 'custom', id: 'vault', keys: [], visible: (s) => s.passwordAutofill },
		toggle('linkPreviews', 'Open links to other sites as a preview', {
			detail: 'It opens nearly full size over the page, so you can look without leaving it.',
		}),
		toggle('linkPreviewsShiftClick', 'Shift-click previews any link', { disabled: (s) => !s.linkPreviews }),
		toggle('linkPreviewsFromNewWindows', 'Links that open a new window preview too', { disabled: (s) => !s.linkPreviews }),
		toggle('blockTrackers', 'Block trackers', {
			detail: `Refuses ${TRACKER_DOMAINS.length} tracking domains and strips tracking parameters.`,
		}),
		toggle('restoreTabs', 'Reopen tabs from last time', {
			detail: (s) => (s.restoreTabs ? 'Your open tabs are saved to a file on this PC. Relaunch to apply.' : undefined),
			warning: (s) => s.restoreTabs,
		}),
	],
	newTab: [
		{ type: 'custom', id: 'shortcuts', keys: ['startShortcuts'] },
		{ type: 'custom', id: 'tool-links', keys: ['toolLinks'] },
	],
	tabs: [
		{
			type: 'setting',
			key: 'tabOrganization',
			title: 'Arrange tabs',
			detail: (s) => tabOrganizationDetail[s.tabOrganization],
			control: { kind: 'menu', options: options(TAB_ORGANIZATIONS, tabOrganizationLabel) },
		},
		{
			type: 'setting',
			key: 'tabDropdowns',
			title: 'Tab style',
			detail: (s) => (s.tabDropdowns ? 'One tab per site. Click it to pick the page.' : 'Every page gets its own tab.'),
			control: {
				kind: 'segmented',
				width: 200,
				options: [
					{ value: false, label: 'Separate' },
					{ value: true, label: 'Dropdown' },
				],
			},
		},
		{
			type: 'setting',
			key: 'tabBarPosition',
			title: 'Tab bar position',
			control: { kind: 'segmented', width: 200, options: options(TAB_BAR_POSITIONS, POSITION_LABELS) },
		},
		{
			type: 'setting',
			key: 'verticalTabWidth',
			// The width of the full rail: collapsed, it is a column of icons.
			title: 'Tab sidebar width',
			detail: 'You can also drag the sidebar’s edge.',
			visible: (s) => s.tabBarPosition !== 'top',
			control: { kind: 'slider', min: VERTICAL_TABS.minWidth, max: VERTICAL_TABS.maxWidth, step: 4, unit: 'px' },
		},
		toggle('verticalTabsCollapsed', 'Collapse the tab sidebar to icons', {
			detail: (s) => (s.tabBarPosition === 'left' ? 'Point at the icons to slide the full sidebar out.' : undefined),
			visible: (s) => s.tabBarPosition !== 'top',
		}),
		toggle('autoCloseEnabled', 'Close inactive tabs', { detail: "Pinned tabs and the one you're on are always kept." }),
		{
			type: 'setting',
			key: 'autoCloseRule',
			title: 'After',
			visible: (s) => s.autoCloseEnabled,
			control: { kind: 'segmented', width: 300, options: options(AUTO_CLOSE_RULES, RULE_LABELS) },
		},
		{
			type: 'setting',
			key: 'autoCloseCustomHours',
			title: 'Custom length',
			visible: (s) => s.autoCloseEnabled && s.autoCloseRule === 'custom',
			control: { kind: 'slider', min: 1, max: 168, step: 1, unit: 'hours' },
		},
		toggle('autoCloseAtTimeEnabled', 'Also close at a set time', { visible: (s) => s.autoCloseEnabled }),
		{
			type: 'setting',
			key: 'autoCloseHour',
			title: 'Hour',
			visible: (s) => s.autoCloseEnabled && s.autoCloseAtTimeEnabled,
			control: { kind: 'menu', options: Array.from({ length: 24 }, (_, h) => ({ value: h, label: pad(h) })) },
		},
		{
			type: 'setting',
			key: 'autoCloseMinute',
			title: 'Minute',
			visible: (s) => s.autoCloseEnabled && s.autoCloseAtTimeEnabled,
			control: { kind: 'menu', options: [0, 15, 30, 45].map((m) => ({ value: m, label: pad(m) })) },
		},
		{ type: 'custom', id: 'sleeping-tabs', keys: ['sleepingTabs'] },
	],
	files: [
		{
			type: 'setting',
			key: 'defaultQuality',
			title: 'Default quality',
			detail: 'For lossy formats.',
			control: { kind: 'slider', min: 10, max: 100, step: 5, unit: '' },
		},
		toggle('preserveOriginals', 'Never overwrite files', {
			detail: (s) => (s.preserveOriginals ? 'A clashing name gets a number added.' : 'A file a tool makes replaces one of the same name in Downloads.'),
			warning: (s) => !s.preserveOriginals,
		}),
		{ type: 'custom', id: 'engines', keys: [] },
	],
	privacy: [
		{ type: 'custom', id: 'privacy-banner', keys: [] },
		toggle('persistentProfile', 'Stay signed in to sites', {
			detail: (s) =>
				s.persistentProfile ? 'Cookies and cache are saved to disk. Relaunch to apply.' : 'Off: every sign-in ends when Zing quits.',
			warning: (s) => s.persistentProfile,
		}),
		{
			type: 'setting',
			key: 'dohProvider',
			title: 'Encrypted DNS',
			detail: "For Zing's own lookups. System-wide is in Network & VPN.",
			control: { kind: 'menu', options: options(DOH_PROVIDERS, DOH_LABELS) },
		},
		{ type: 'custom', id: 'recent-tools', keys: ['recentFeatures'] },
		{ type: 'custom', id: 'clear-session', keys: [] },
	],
	security: [
		toggle('securityRealtime', 'Inspect links before they load', { detail: 'Warns about look-alike and deceptive addresses.' }),
	],
	advanced: [
		toggle('acceleratedCanvas', 'Draw canvas on the GPU', { detail: 'Faster on canvas-heavy pages. Relaunch to apply.' }),
	],
	account: [{ type: 'custom', id: 'account', keys: [] }],
	about: [{ type: 'custom', id: 'about', keys: [] }],
	import: [{ type: 'custom', id: 'import', keys: [] }],
}

/** Every setting key some control edits, hidden rows included. */
export function boundKeys(): Set<SettingKey> {
	const keys = new Set<SettingKey>()
	keys.add(CHECK_FOR_UPDATES_ROW.key)
	for (const items of Object.values(PANES)) {
		for (const item of items) {
			if (item.type === 'setting') keys.add(item.key)
			else for (const key of item.keys) keys.add(key)
		}
	}
	return keys
}

export function isVisible(item: PaneItem, settings: Settings): boolean {
	return item.visible ? item.visible(settings) : true
}

export function detailOf(row: SettingRow, settings: Settings): string | undefined {
	return typeof row.detail === 'function' ? row.detail(settings) : row.detail
}
