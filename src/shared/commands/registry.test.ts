import { describe, expect, it } from 'vitest'
import { COMMAND_IDS, COMMANDS, MENU_LAYOUT, commandForInput, getCommand, isCommandId } from './registry'
import { shortcutLabel, toAccelerator, type KeyInput } from './shortcut'

// Every menu item in reference/krackn/Sources/Krackn/App/Commands.swift, plus Ctrl+1–9 from AlternateShortcuts.
const KRACKN_LABELS = [
	'New Tab',
	'New Window',
	'Open Location…',
	'Close Tab',
	'Close Window',
	'Reopen Last Closed Tab',
	'Print…',
	'Find…',
	'Find Next',
	'Find Previous',
	'Settings…',
	'Toggle Sidebar',
	'Toggle Favourites Bar',
	'Show Tab Overview',
	'Show Reader',
	'Reload Page',
	'Stop',
	'Zoom In',
	'Zoom Out',
	'Actual Size',
	'Make Text Bigger',
	'Make Text Smaller',
	'Back',
	'Forward',
	'Home',
	'Add to Favourites',
	'Add to Reading List',
	'Show Bookmarks',
	'Show Reading List',
	'Show Previous Tab',
	'Show Next Tab',
]

const key = (code: string, mods: Partial<KeyInput> = {}): KeyInput => ({
	code,
	control: false,
	shift: false,
	alt: false,
	meta: false,
	...mods,
})

describe('command registry', () => {
	it('has every Krackn command', () => {
		const labels = COMMANDS.map((c) => c.label)
		for (const label of KRACKN_LABELS) expect(labels).toContain(label)
	})

	it('has Ctrl+1 to Ctrl+9', () => {
		for (let n = 1; n <= 9; n++) {
			expect(commandForInput(key(`Digit${n}`, { control: true }))?.id).toBe(`tab.select${n}`)
		}
	})

	it('defines every id exactly once, and only registered ids', () => {
		expect(COMMANDS.map((c) => c.id).sort()).toEqual([...COMMAND_IDS].sort())
	})

	it('gives every command at least one shortcut', () => {
		for (const c of COMMANDS) expect(c.shortcuts.length, c.id).toBeGreaterThan(0)
	})

	it('never binds one shortcut to two commands', () => {
		const owners = new Map<string, string>()
		for (const c of COMMANDS) {
			for (const s of c.shortcuts) {
				const accel = toAccelerator(s)
				expect(owners.get(accel), `${accel} on ${c.id}`).toBeUndefined()
				owners.set(accel, c.id)
			}
		}
	})

	it('uses Windows conventions', () => {
		expect(commandForInput(key('Tab', { control: true }))?.id).toBe('tab.next')
		expect(commandForInput(key('Tab', { control: true, shift: true }))?.id).toBe('tab.previous')
		expect(commandForInput(key('F5'))?.id).toBe('page.reload')
		expect(commandForInput(key('ArrowLeft', { alt: true }))?.id).toBe('history.back')
		expect(commandForInput(key('KeyT', { control: true, shift: true }))?.id).toBe('tab.reopenClosed')
		expect(commandForInput(key('Equal', { control: true }))?.id).toBe('page.zoomIn')
	})

	it('ignores the Windows key and plain typing', () => {
		expect(commandForInput(key('KeyT', { control: true, meta: true }))).toBeUndefined()
		expect(commandForInput(key('KeyT'))).toBeUndefined()
		expect(commandForInput(key('KeyT', { shift: true }))).toBeUndefined()
	})

	it('formats accelerators for Electron and labels for people', () => {
		expect(toAccelerator(getCommand('tab.reopenClosed').shortcuts[0]!)).toBe('Ctrl+Shift+T')
		expect(toAccelerator(getCommand('history.back').shortcuts[0]!)).toBe('Alt+Left')
		expect(toAccelerator(getCommand('page.zoomIn').shortcuts[2]!)).toBe('Ctrl+numadd')
		expect(shortcutLabel(getCommand('page.zoomIn').shortcuts[2]!)).toBe('Ctrl+Num +')
	})

	it('only lays out registered commands in menus', () => {
		for (const section of MENU_LAYOUT) {
			for (const item of section.items) {
				if (item !== 'separator') expect(isCommandId(item), item).toBe(true)
			}
		}
	})

	it('rejects unknown ids', () => {
		expect(isCommandId('tab.explode')).toBe(false)
		expect(isCommandId(42)).toBe(false)
	})
})
