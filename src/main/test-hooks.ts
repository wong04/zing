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
	if (app.isPackaged || process.env['ZING_E2E'] !== '1') return
	const record = (kind: string, error: unknown): void => {
		const text = error instanceof Error ? (error.stack ?? error.message) : String(error)
		appendFileSync(join(app.getPath('userData'), MAIN_ERRORS_FILE), `${kind}: ${text}\n\n`)
	}
	process.on('uncaughtException', (error) => record('uncaughtException', error))
	process.on('unhandledRejection', (reason) => record('unhandledRejection', reason))
}

/**
 * Where the mouse is, for edge reveal. In e2e runs the real pointer would make
 * results depend on wherever the person at the PC left it (a pointer near the
 * left screen edge kept the sidebar revealed and failed a test), so tests get
 * a pointer far off-screen that they can move by setting
 * `globalThis.__zingTestCursor` (screen coordinates) through `app.evaluate`.
 */
export function cursorPoint(): Electron.Point {
	if (app.isPackaged || process.env['ZING_E2E'] !== '1') return screen.getCursorScreenPoint()
	const hooks = globalThis as unknown as { __zingTestCursor?: Electron.Point }
	return hooks.__zingTestCursor ?? { x: -100_000, y: -100_000 }
}
