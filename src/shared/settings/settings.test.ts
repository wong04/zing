import { describe, expect, expectTypeOf, it } from 'vitest'
import { DEFAULT_SETTINGS, SETTING_KEYS, SETTINGS_SCHEMA, acceptsValue, type SettingKey, type Settings } from './schema'
import vectors from './settings.vectors.json'
import { InvalidSettingError, SettingsStore, UnknownSettingError, changedSettings, parseSettings } from './store'

// The `static let` names in AppSettings.Key, reference/krackn/Sources/Krackn/App/AppSettings.swift.
const KRACKN_KEYS = vectors.kracknKeys

describe('settings schema', () => {
	it('has all 30 Krackn keys plus Zing tabBarPosition', () => {
		expect(KRACKN_KEYS).toHaveLength(30)
		expect([...SETTING_KEYS].sort()).toEqual([...KRACKN_KEYS, ...vectors.zingKeys].sort())
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
		expect(DEFAULT_SETTINGS).toMatchObject(vectors.defaultsSubset as Partial<Settings>)
	})
})

const ERRORS = { InvalidSetting: InvalidSettingError, UnknownSetting: UnknownSettingError }

describe('parseSettings', () => {
	for (const { name, input, expectSubset } of vectors.parseAccepted) {
		it(name, () => {
			expect(parseSettings(input)).toMatchObject(expectSubset)
		})
	}

	it('rejects invalid values and unknown keys instead of replacing them', () => {
		for (const { input, error } of vectors.parseRejected) {
			expect(() => parseSettings(input), JSON.stringify(input)).toThrow(ERRORS[error as keyof typeof ERRORS])
		}
	})
})

describe('acceptsValue', () => {
	for (const { key, value, ok } of vectors.accepts) {
		it(`${key} ${ok ? 'accepts' : 'rejects'} ${JSON.stringify(value)}`, () => {
			expect(acceptsValue(key as SettingKey, value)).toBe(ok)
		})
	}
})

describe('SettingsStore', () => {
	it('validates writes and notifies listeners', () => {
		const store = new SettingsStore()
		const seen: string[] = []
		store.subscribe((_, key) => seen.push(key))
		store.set('tabBarPosition', 'left')
		expect(store.get('tabBarPosition')).toBe('left')
		expect(seen).toEqual(['tabBarPosition'])
		for (const { key, value, unchanged } of vectors.storeRejects) {
			expect(() => store.set(key as SettingKey, value as never)).toThrow(InvalidSettingError)
			expect(store.get(key as SettingKey)).toBe(unchanged)
		}
	})

	it('persists only what differs from the defaults', () => {
		for (const { set, changed } of vectors.changed) {
			const store = new SettingsStore()
			for (const [key, value] of Object.entries(set)) store.set(key as SettingKey, value as never)
			expect(changedSettings(store.snapshot())).toEqual(changed)
		}
	})
})
