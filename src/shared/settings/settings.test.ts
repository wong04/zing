import { describe, expect, expectTypeOf, it } from 'vitest'
import { DEFAULT_SETTINGS, SETTING_KEYS, SETTINGS_SCHEMA, acceptsValue, type Settings } from './schema'
import { InvalidSettingError, SettingsStore, UnknownSettingError, changedSettings, parseSettings } from './store'

// The `static let` names in AppSettings.Key, reference/krackn/Sources/Krackn/App/AppSettings.swift.
const KRACKN_KEYS = [
	'theme',
	'accent',
	'homepage',
	'startShortcuts',
	'searchEngine',
	'blockTrackers',
	'confirmQuit',
	'defaultQuality',
	'preserveOriginals',
	'dohProvider',
	'tabOrganization',
	'autoCloseEnabled',
	'autoCloseRule',
	'autoCloseCustomHours',
	'autoCloseAtTimeEnabled',
	'autoCloseHour',
	'autoCloseMinute',
	'persistentProfile',
	'defaultUserAgent',
	'acceleratedCanvas',
	'restoreTabs',
	'securityRealtime',
	'securityDownloadScan',
	'securityDomainBlocking',
	'securitySignatureCheck',
	'sidebarPinned',
	'recentFeatures',
	'showFavouritesBar',
	'passwordAutofill',
	'tabDropdowns',
]

describe('settings schema', () => {
	it('has all 30 Krackn keys plus Zing tabBarPosition', () => {
		expect(KRACKN_KEYS).toHaveLength(30)
		expect([...SETTING_KEYS].sort()).toEqual([...KRACKN_KEYS, 'tabBarPosition', 'sleepingTabs'].sort())
	})

	it('gives every key a default its own validator accepts, readable through the typed accessor', () => {
		const store = new SettingsStore()
		for (const key of SETTING_KEYS) {
			expect(Object.hasOwn(DEFAULT_SETTINGS, key), key).toBe(true)
			expect(DEFAULT_SETTINGS[key], key).not.toBeUndefined()
			expect(acceptsValue(key, SETTINGS_SCHEMA[key].default), key).toBe(true)
			expect(store.get(key), key).toEqual(SETTINGS_SCHEMA[key].default)
		}
	})

	it('types each accessor by key', () => {
		const store = new SettingsStore()
		expectTypeOf(store.get('blockTrackers')).toEqualTypeOf<boolean>()
		expectTypeOf(store.get('defaultQuality')).toEqualTypeOf<number>()
		expectTypeOf(store.get('tabBarPosition')).toEqualTypeOf<'top' | 'left' | 'right'>()
		expectTypeOf(store.get('theme')).toEqualTypeOf<'system' | 'light' | 'dark'>()
		expectTypeOf(store.get('recentFeatures')).toEqualTypeOf<readonly string[]>()
	})

	it('keeps Krackn defaults, protective switches on and data-keeping switches off', () => {
		expect(DEFAULT_SETTINGS).toMatchObject({
			theme: 'system',
			accent: 'yellow',
			searchEngine: 'google',
			homepage: '',
			startShortcuts: null,
			blockTrackers: true,
			preserveOriginals: true,
			defaultQuality: 80,
			dohProvider: 'cloudflare',
			tabOrganization: 'manual',
			autoCloseRule: 'day',
			autoCloseCustomHours: 12,
			autoCloseHour: 3,
			autoCloseMinute: 0,
			acceleratedCanvas: true,
			securityRealtime: true,
			securityDownloadScan: true,
			securityDomainBlocking: true,
			securitySignatureCheck: true,
			sidebarPinned: true,
			showFavouritesBar: true,
			passwordAutofill: true,
			persistentProfile: false,
			restoreTabs: false,
			confirmQuit: false,
			tabDropdowns: false,
			tabBarPosition: 'top',
		} satisfies Partial<Settings>)
	})
})

describe('parseSettings', () => {
	it('reads an absent key as its default, never as false', () => {
		const parsed = parseSettings({ confirmQuit: true })
		expect(parsed.confirmQuit).toBe(true)
		expect(parsed.preserveOriginals).toBe(true)
		expect(parsed.blockTrackers).toBe(true)
	})

	it('rejects invalid values and unknown keys instead of replacing them', () => {
		expect(() => parseSettings({ defaultQuality: 400 })).toThrow(InvalidSettingError)
		expect(() => parseSettings({ autoCloseHour: 3.5 })).toThrow(InvalidSettingError)
		expect(() => parseSettings({ tabBarPosition: 'bottom' })).toThrow(InvalidSettingError)
		expect(() => parseSettings({ history: true })).toThrow(UnknownSettingError)
		expect(() => parseSettings([])).toThrow(InvalidSettingError)
	})
})

describe('SettingsStore', () => {
	it('validates writes and notifies listeners', () => {
		const store = new SettingsStore()
		const seen: string[] = []
		store.subscribe((_, key) => seen.push(key))
		store.set('tabBarPosition', 'left')
		expect(store.get('tabBarPosition')).toBe('left')
		expect(seen).toEqual(['tabBarPosition'])
		expect(() => store.set('defaultQuality', 5)).toThrow(InvalidSettingError)
		expect(store.get('defaultQuality')).toBe(80)
	})

	it('persists only what differs from the defaults', () => {
		const store = new SettingsStore()
		store.set('restoreTabs', true)
		expect(changedSettings(store.snapshot())).toEqual({ restoreTabs: true })
	})
})
