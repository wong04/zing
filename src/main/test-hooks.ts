import { appendFileSync } from 'node:fs'
import { join } from 'node:path'
import { app, screen } from 'electron'

export const MAIN_ERRORS_FILE = 'main-errors.log'

/**
 * End-to-end runs only: every uncaught main-process error is appended to a
 * file in the profile, and the e2e harness fails the test if that file exists
 * when the app closes. Phase 3's crashes (an init-order bug, a destroyed page
 * read) only surfaced as dialogs when someone ran the app by hand; this makes
 * any of them fail CI instead. Registering the handlers also keeps Electron's
 * blocking error dialog from hanging a test run.
 */
export function recordMainErrorsForTests(): void {
	if (!isEndToEndRun()) return
	process.on('uncaughtException', (error) => recordMainError('uncaughtException', error))
	process.on('unhandledRejection', (reason) => recordMainError('unhandledRejection', reason))
}

/** Writes one error where the e2e harness will find it. */
export function recordMainError(kind: string, error: unknown): void {
	const text = error instanceof Error ? (error.stack ?? error.message) : String(error)
	appendFileSync(join(app.getPath('userData'), MAIN_ERRORS_FILE), `${kind}: ${text}\n\n`)
}

/**
 * Where the mouse is, for edge reveal. In e2e runs the real pointer would make
 * results depend on wherever the person at the PC left it (a pointer near the
 * left screen edge kept the sidebar revealed and failed a test), so tests get
 * a pointer far off-screen that they can move by setting
 * `globalThis.__zingTestCursor` (screen coordinates) through `app.evaluate`.
 */
export function cursorPoint(): Electron.Point {
	if (!isEndToEndRun()) return screen.getCursorScreenPoint()
	const hooks = globalThis as unknown as { __zingTestCursor?: Electron.Point }
	return hooks.__zingTestCursor ?? { x: -100_000, y: -100_000 }
}

export function isEndToEndRun(): boolean {
	return !app.isPackaged && process.env['ZING_E2E'] === '1'
}

/**
 * e2e runs open dozens of windows while the user may be working at the same
 * PC. They go far off-screen and out of the taskbar, and are shown without
 * taking focus (see showWindow), so a test run never pops up or steals typing.
 */
export function testWindowPlacement(): Partial<Electron.BrowserWindowConstructorOptions> {
	if (!isEndToEndRun()) return {}
	return { x: -20_000, y: -20_000, skipTaskbar: true }
}

export function showWindow(window: Electron.BrowserWindow): void {
	if (isEndToEndRun()) window.showInactive()
	else window.show()
}

/**
 * Chromium stops painting a window it considers covered, and an off-screen
 * window counts, which would stall page visibility, timers and captures in
 * e2e. Must run before the app is ready.
 */
export function keepOffscreenTestWindowsPainting(): void {
	if (!isEndToEndRun()) return
	app.commandLine.appendSwitch('disable-features', 'CalculateNativeWinOcclusion')
}
