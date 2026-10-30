import { existsSync } from 'node:fs'
import { mkdir, mkdtemp, writeFile } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { _electron as electron, type ElectronApplication, type Page } from '@playwright/test'
import { ElectronBlocker } from '@ghostery/adblocker-electron'

// Playwright lists every webContents as a window, including tab pages and the
// overlay, so the chrome renderer is found by its URL rather than by order.
export function isChrome(page: Page): boolean {
	return URL.canParse(page.url()) && /\/renderer\/index\.html#chrome$/.test(page.url())
}

const CACHE_FILE = 'adblock-engine.bin'

export interface LaunchOptions {
	/** Extra command-line arguments after the app path, e.g. a URL or a Chromium switch. */
	readonly args?: readonly string[]
	/** Reuse a profile folder (needed to start a second instance of the same app). */
	readonly userDataDir?: string
	/** Written to settings.json before launch, e.g. `{ confirmQuit: true }`. */
	readonly settings?: Readonly<Record<string, unknown>>
	/** Extra environment variables for the app, e.g. `ZING_TEST_CLOCK: '1'` to drive timers. */
	readonly env?: Readonly<Record<string, string>>
	/** Network filter rules the ad blocker starts with. Defaults to none. */
	readonly filters?: string
}

export interface Launched {
	readonly app: ElectronApplication
	readonly chrome: Page
	readonly userDataDir: string
}

/**
 * Seeds a fresh engine cache, so the blocker neither waits for nor needs the
 * network: its cache is under a day old, which skips the background refresh.
 */
async function seedBlocklist(userDataDir: string, filters: string): Promise<void> {
	await mkdir(userDataDir, { recursive: true })
	const engine = ElectronBlocker.parse(filters, { loadCosmeticFilters: false, loadNetworkFilters: true })
	await writeFile(join(userDataDir, CACHE_FILE), engine.serialize())
}

export async function launchZing(options: LaunchOptions = {}): Promise<Launched> {
	const userDataDir = options.userDataDir ?? (await mkdtemp(join(tmpdir(), 'zing-e2e-')))
	if (!existsSync(join(userDataDir, CACHE_FILE))) await seedBlocklist(userDataDir, options.filters ?? '')
	if (options.settings) await writeFile(join(userDataDir, 'settings.json'), JSON.stringify(options.settings))
	const app = await electron.launch({
		args: ['.', `--user-data-dir=${userDataDir}`, ...(options.args ?? [])],
		env: { ...(process.env as Record<string, string>), ZING_SKIP_ACCOUNT_GATE: '1', ...options.env },
	})
	const existing = app.windows().find(isChrome)
	const chrome = existing ?? (await app.waitForEvent('window', { predicate: isChrome }))
	await chrome.waitForLoadState('domcontentloaded')
	return { app, chrome, userDataDir }
}
