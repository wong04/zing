import { join } from 'node:path'
import { app, nativeImage, nativeTheme, systemPreferences, type BrowserWindow, type NativeImage } from 'electron'
import { runProcess } from '../platform/process'

export type TaskbarTheme = 'light' | 'dark'
export type IconTile = 'frosted' | 'dark-rim'

// Windows has two theme switches: "app mode" (what nativeTheme follows, and
// what Zing's own theme setting overrides) and "Windows mode", which colours
// the taskbar. The icon sits on the taskbar, so it follows Windows mode.
const PERSONALIZE_KEY = 'HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize'

// Contrast, not match: the light frosted tile reads on a dark taskbar, the
// dark tile on a light one. The .exe, Start menu and pinned shortcut can't
// change at runtime, so they always use the frosted tile (build/icons).
export const TILE_FOR_TASKBAR: Record<TaskbarTheme, IconTile> = { dark: 'frosted', light: 'dark-rim' }

const ICON_DIRS: Record<IconTile, string> = { frosted: 'icons', 'dark-rim': 'icons-dark-tile' }

export class TaskbarThemeError extends Error {
	override name = 'TaskbarThemeError'
}

// `reg query` prints e.g. "    SystemUsesLightTheme    REG_DWORD    0x1".
export function parseSystemUsesLightTheme(output: string): TaskbarTheme {
	const match = /SystemUsesLightTheme\s+REG_DWORD\s+0x([0-9a-f]+)/i.exec(output)
	if (!match) throw new TaskbarThemeError(`Unexpected registry output: ${output.trim()}`)
	return parseInt(match[1]!, 16) === 0 ? 'dark' : 'light'
}

export async function readTaskbarTheme(): Promise<TaskbarTheme> {
	const result = await runProcess('reg.exe', ['query', PERSONALIZE_KEY, '/v', 'SystemUsesLightTheme'], { timeoutMs: 5000 })
	// Windows versions before the light taskbar (pre-1903) don't have the
	// value at all, and their taskbar is always dark.
	if (result.exitCode !== 0 && /unable to find/i.test(result.stderr)) return 'dark'
	if (result.exitCode !== 0) throw new TaskbarThemeError(`reg.exe exited ${result.exitCode}: ${result.stderr.trim()}`)
	return parseSystemUsesLightTheme(result.stdout)
}

function iconRoot(): string {
	return app.isPackaged ? process.resourcesPath : join(app.getAppPath(), 'build')
}

function loadIcon(tile: IconTile): NativeImage {
	const path = join(iconRoot(), ICON_DIRS[tile], 'icon.ico')
	const image = nativeImage.createFromPath(path)
	if (image.isEmpty()) throw new TaskbarThemeError(`Icon missing or unreadable: ${path}`)
	return image
}

// Keeps every window's taskbar button (and Alt+Tab icon) on the tile that
// contrasts with the current taskbar.
export class TaskbarIcon {
	readonly #windows = new Set<BrowserWindow>()
	readonly #images: Record<IconTile, NativeImage>
	#tile: IconTile = TILE_FOR_TASKBAR.dark

	constructor() {
		this.#images = { frosted: loadIcon('frosted'), 'dark-rim': loadIcon('dark-rim') }
		const recheck = () => void this.refresh()
		nativeTheme.on('updated', recheck)
		systemPreferences.on('color-changed', recheck)
	}

	attach(window: BrowserWindow): void {
		this.#windows.add(window)
		window.setIcon(this.#images[this.#tile])
		// A cheap backstop for theme changes the events above don't report
		// while Zing's own theme setting overrides nativeTheme.
		window.on('focus', () => void this.refresh())
		window.once('closed', () => this.#windows.delete(window))
	}

	async refresh(): Promise<void> {
		const tile = TILE_FOR_TASKBAR[await readTaskbarTheme()]
		if (tile === this.#tile) return
		this.#tile = tile
		for (const window of this.#windows) window.setIcon(this.#images[tile])
	}
}
