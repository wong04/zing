import { describe, expect, it } from 'vitest'
import { DEFAULT_SETTINGS, SETTING_KEYS, acceptsValue } from '@shared/settings'
import { SETTINGS_CATEGORIES } from '@shared/shell/shellState'
import { boundKeys, CATEGORIES, CHECK_FOR_UPDATES_ROW, PANES, detailOf, isVisible, type SettingRow } from './rows'

describe('settings rows', () => {
	it('binds every setting in the schema to a control', () => {
		const bound = boundKeys()
		// Kept so older settings files still load; WebView2 always presents Edge's own user agent.
		const retired = new Set(['defaultUserAgent'])
		// Zing's own bookkeeping, set by the app rather than the person; none of these ever gets a row.
		const internal = new Set(['launchedBefore', 'defaultBrowserDismissed', 'importNudgeDone'])
		expect(SETTING_KEYS.filter((k) => !bound.has(k) && !retired.has(k) && !internal.has(k))).toEqual([])
	})

	it('puts the update switch in About, saying what it sends', () => {
		const row = CHECK_FOR_UPDATES_ROW
		expect(row.key).toBe('checkForUpdates')
		expect(boundKeys().has('checkForUpdates')).toBe(true)
		expect(row.control.kind).toBe('toggle')
		expect(detailOf(row, DEFAULT_SETTINGS)).toBe('Asks GitHub for the latest version every few hours. Sends nothing about you.')
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

	// The switch's detail shows whatever sync is doing, so it must be true with sync on too (Task 15 text check).
	it('says the vault is on this PC and, with sync on, encrypted on the sync server', () => {
		const row = PANES.browsing.find((i) => i.type === 'setting' && i.key === 'passwordAutofill')
		if (row?.type !== 'setting') throw new Error('No passwordAutofill row')
		for (const passwordAutofill of [false, true]) {
			expect(detailOf(row, { ...DEFAULT_SETTINGS, passwordAutofill })).toBe(
				'A key appears in sign-in fields. Passwords are kept in the vault on this PC, and with sync on, an encrypted copy is kept on the sync server.',
			)
		}
	})
})
