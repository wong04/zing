import type { BrowserWindow, WebContents, WebContentsView } from 'electron'

// What Electron reports when the page or its window went away mid-request:
// a tab closed while its favicon probe ran, a window shut while an overlay was
// still loading. Found by the e2e main-process guard (src/main/test-hooks.ts):
// these were unhandled rejections in nine Phase 3 tests.
const GONE_MESSAGES = [/Render frame was disposed/i, /Object has been destroyed/i, /WebContents was destroyed/i, /\bERR_ABORTED\b/]

/**
 * True when a failure only means the page is gone. Everything else is a real
 * error and must still surface; this never swallows those.
 */
export function isGoneError(contents: WebContents, error: unknown): boolean {
	if (contents.isDestroyed()) return true
	const message = error instanceof Error ? error.message : String(error)
	return GONE_MESSAGES.some((pattern) => pattern.test(message))
}

/** Loads one of Zing's own renderer pages; a view torn down mid-load is not an error. */
export async function loadUnlessGone(contents: WebContents, load: (contents: WebContents) => Promise<void>): Promise<void> {
	try {
		await load(contents)
	} catch (error) {
		if (!isGoneError(contents, error)) throw error
	}
}

export type PageScriptResult<T> = { readonly status: 'ran'; readonly value: T } | { readonly status: 'gone' }

/** Runs a script in a page that may navigate or close while it runs. */
export async function runInPage<T>(contents: WebContents, script: string): Promise<PageScriptResult<T>> {
	if (contents.isDestroyed()) return { status: 'gone' }
	try {
		return { status: 'ran', value: (await contents.executeJavaScript(script)) as T }
	} catch (error) {
		if (isGoneError(contents, error)) return { status: 'gone' }
		throw error
	}
}

/** The same, in an isolated world the page's own scripts can't see. */
export async function runInIsolatedWorld<T>(contents: WebContents, world: number, script: string): Promise<PageScriptResult<T>> {
	if (contents.isDestroyed()) return { status: 'gone' }
	try {
		return { status: 'ran', value: (await contents.executeJavaScriptInIsolatedWorld(world, [{ code: script }])) as T }
	} catch (error) {
		if (isGoneError(contents, error)) return { status: 'gone' }
		throw error
	}
}

/**
 * Whether a view (and the window it sits in) can still be used. Check it after
 * every `await` that waits on a view: the window can close while it loads, and
 * a destroyed view's `webContents` may already be detached.
 */
export function isViewLive(window: BrowserWindow, view: WebContentsView): boolean {
	const contents = view.webContents as WebContents | undefined
	return !window.isDestroyed() && contents !== undefined && !contents.isDestroyed()
}
