import { describe, expect, it } from 'vitest'
import { DEFAULT_SETTINGS, SETTING_KEYS, acceptsValue } from '@shared/settings'
import { SETTINGS_CATEGORIES } from '@shared/shell/shellState'
import { boundKeys, CATEGORIES, PANES, isVisible, type SettingRow } from './rows'

describe('settings rows', () => {
	it('binds every setting in the schema to a control', () => {
		const bound = boundKeys()
		// Kept so older settings files still load; WebView2 always presents Edge's own user agent.
		const retired = new Set(['defaultUserAgent'])
		expect(SETTING_KEYS.filter((k) => !bound.has(k) && !retired.has(k))).toEqual([])
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

	it('disables the other two link preview switches while the first is off', () => {
		const rows = PANES.browsing.filter((i) => i.type === 'setting' && i.key.startsWith('linkPreviews')) as SettingRow[]
		expect(rows.map((r) => r.key)).toEqual(['linkPreviews', 'linkPreviewsShiftClick', 'linkPreviewsFromNewWindows'])
		const off = { ...DEFAULT_SETTINGS, linkPreviews: false }
		expect(rows.map((r) => r.disabled?.(DEFAULT_SETTINGS) ?? false)).toEqual([false, false, false])
		expect(rows.map((r) => r.disabled?.(off) ?? false)).toEqual([false, true, true])
	})

	it('describes the preview as it is: nearly the whole page area, not a small window', () => {
		const row = PANES.browsing.find((i) => i.type === 'setting' && i.key === 'linkPreviews') as SettingRow
		const detail = typeof row.detail === 'function' ? row.detail(DEFAULT_SETTINGS) : row.detail
		expect(detail).not.toMatch(/small/i)
		expect(detail).toMatch(/nearly full size/i)
	})

	it('puts the saved passwords right after their switch, shown only while it is on', () => {
		const items = PANES.browsing
		const at = items.findIndex((i) => i.type === 'setting' && i.key === 'passwordAutofill')
		const vault = items[at + 1]
		expect(vault).toMatchObject({ type: 'custom', id: 'vault' })
		expect(isVisible(vault!, DEFAULT_SETTINGS)).toBe(false)
		expect(isVisible(vault!, { ...DEFAULT_SETTINGS, passwordAutofill: true })).toBe(true)
	})
})
