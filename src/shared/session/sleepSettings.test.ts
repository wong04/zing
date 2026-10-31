// What `sleepingTabs` accepts lives in sleepSettings.vectors.json, which the C# port's tests read too.
import { describe, expect, it } from 'vitest'
import { DEFAULT_SETTINGS, acceptsValue, changedSettings } from '../settings'
import { InvalidSettingError, SettingsStore, parseSettings } from '../settings/store'
import { DEFAULT_SLEEPING_TABS, isSleepSettings } from './sleepSettings'
import vectors from './sleepSettings.vectors.json'

describe('sleepingTabs setting', () => {
	it('defaults to on, 30 minutes, pinned tabs kept awake', () => {
		expect(DEFAULT_SETTINGS.sleepingTabs).toEqual(vectors.default)
		expect(isSleepSettings(DEFAULT_SLEEPING_TABS)).toBe(true)
	})

	it.each(vectors.valid)('accepts %j', (value) => {
		expect(acceptsValue('sleepingTabs', value)).toBe(true)
		expect(isSleepSettings(value)).toBe(true)
	})

	it.each(vectors.invalid as unknown[])('rejects %j', (value) => {
		expect(isSleepSettings(value)).toBe(false)
	})

	it('persists only when it differs from the default, and refuses invalid writes', () => {
		const store = new SettingsStore()
		expect(changedSettings(store.snapshot())).toEqual({})
		store.set('sleepingTabs', { enabled: true, afterMinutes: 10, sleepPinned: false })
		expect(changedSettings(store.snapshot())).toEqual({ sleepingTabs: { enabled: true, afterMinutes: 10, sleepPinned: false } })
		expect(() => store.set('sleepingTabs', { enabled: true, afterMinutes: -1, sleepPinned: false })).toThrow(InvalidSettingError)
		expect(parseSettings({ sleepingTabs: { enabled: false, afterMinutes: 60, sleepPinned: true } }).sleepingTabs.afterMinutes).toBe(60)
	})
})
