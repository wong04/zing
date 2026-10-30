import { describe, expect, it } from 'vitest'
import { DEFAULT_SETTINGS, SETTING_KEYS, acceptsValue } from '@shared/settings'
import { SETTINGS_CATEGORIES } from '@shared/shell/shellState'
import { boundKeys, CATEGORIES, PANES, isVisible } from './rows'

describe('settings rows', () => {
	it('binds every setting in the schema to a control', () => {
		const bound = boundKeys()
		expect(SETTING_KEYS.filter((k) => !bound.has(k))).toEqual([])
	})

	it('has a pane for every category and no extras', () => {
		expect(CATEGORIES.map((c) => c.id)).toEqual([...SETTINGS_CATEGORIES])
		expect(Object.keys(PANES).sort()).toEqual([...SETTINGS_CATEGORIES].sort())
	})

	it('offers only values the schema accepts', () => {
		for (const items of Object.values(PANES)) {
			for (const item of items) {
				if (item.type !== 'setting' || (item.control.kind !== 'segmented' && item.control.kind !== 'menu')) continue
				for (const option of item.control.options) expect(acceptsValue(item.key, option.value)).toBe(true)
			}
		}
	})

	it('keeps slider ranges inside what the schema accepts', () => {
		for (const items of Object.values(PANES)) {
			for (const item of items) {
				if (item.type !== 'setting' || item.control.kind !== 'slider') continue
				expect(acceptsValue(item.key, item.control.min)).toBe(true)
				expect(acceptsValue(item.key, item.control.max)).toBe(true)
			}
		}
	})

	it('shows the auto-close sub-settings only when auto-close is on', () => {
		const after = PANES.tabs.find((i) => i.type === 'setting' && i.key === 'autoCloseRule')!
		expect(isVisible(after, DEFAULT_SETTINGS)).toBe(false)
		expect(isVisible(after, { ...DEFAULT_SETTINGS, autoCloseEnabled: true })).toBe(true)
	})
})
