import { describe, expect, it, vi } from 'vitest'

vi.mock('electron', () => ({ app: {}, nativeImage: {}, nativeTheme: {}, systemPreferences: {} }))

const { parseSystemUsesLightTheme, readTaskbarTheme, TaskbarThemeError, TILE_FOR_TASKBAR } = await import('./taskbar-icon')

describe('taskbar theme', () => {
	it('reads the registry value', () => {
		expect(parseSystemUsesLightTheme('\r\nHKEY_CURRENT_USER\\...\\Personalize\r\n    SystemUsesLightTheme    REG_DWORD    0x1\r\n')).toBe('light')
		expect(parseSystemUsesLightTheme('    SystemUsesLightTheme    REG_DWORD    0x0')).toBe('dark')
	})
	it('refuses output it does not understand', () => expect(() => parseSystemUsesLightTheme('garbage')).toThrow(TaskbarThemeError))
	it('picks the tile that contrasts with the taskbar', () => {
		expect(TILE_FOR_TASKBAR.dark).toBe('frosted')
		expect(TILE_FOR_TASKBAR.light).toBe('dark-rim')
	})
	// Starts reg.exe, which can be slow to launch on a busy CI runner.
	it('reads this PC’s taskbar theme', async () => expect(['light', 'dark']).toContain(await readTaskbarTheme()), 30_000)
})
