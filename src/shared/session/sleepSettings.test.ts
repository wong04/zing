import { describe, expect, it } from 'vitest'
import { DEFAULT_SETTINGS, acceptsValue, changedSettings } from '../settings'
import { InvalidSettingError, SettingsStore, parseSettings } from '../settings/store'
import { DEFAULT_SLEEPING_TABS, isSleepSettings } from './sleepSettings'

describe('sleepingTabs setting', () => {
	it('defaults to on, 30 minutes, pinned tabs kept awake', () => {
		expect(DEFAULT_SETTINGS.sleepingTabs).toEqual({ enabled: true, afterMinutes: 30, sleepPinned: false })
		expect(isSleepSettings(DEFAULT_SLEEPING_TABS)).toBe(true)
	})

	it('accepts a valid group and rejects malformed ones', () => {
		expect(acceptsValue('sleepingTabs', { enabled: false, afterMinutes: 5, sleepPinned: true })).toBe(true)
		const bad: unknown[] = [
			null,
			[],
			{ enabled: true, afterMinutes: 30 },
			{ enabled: true, afterMinutes: 0, sleepPinned: false },
			{ enabled: true, afterMinutes: 99_999, sleepPinned: false },
			{ enabled: 'yes', afterMinutes: 30, sleepPinned: false },
			{ enabled: true, afterMinutes: 30, sleepPinned: false, extra: 1 },
		]
		for (const value of bad) expect(isSleepSettings(value), JSON.stringify(value)).toBe(false)
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
