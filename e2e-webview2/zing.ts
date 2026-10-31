import { spawn, type ChildProcess } from 'node:child_process'
import { existsSync, readFileSync } from 'node:fs'
import { mkdtemp } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { chromium, expect, type Browser, type Page } from '@playwright/test'

const ROOT = join(import.meta.dirname, '..')
export const HOST_EXE = join(ROOT, 'host', 'Zing.Host', 'bin', 'Release', 'net9.0-windows10.0.22621.0', 'win-x64', 'Zing.exe')
const CHROME_URL = 'https://app.zing/index.html#chrome'

export class HostProcessError extends Error {
	override name = 'HostProcessError'
}

export interface Launched {
	readonly browser: Browser
	readonly chrome: Page
	close(): Promise<void>
}

let nextPort = 9700

/**
 * Starts Zing's WebView2 host off-screen (ZING_E2E) and attaches over CDP.
 * `close()` fails the test if the host recorded any unhandled error, the
 * WebView2 counterpart of e2e/zing.ts's main-process guard.
 */
export async function launchZing(env: Readonly<Record<string, string>> = {}): Promise<Launched> {
	if (!existsSync(HOST_EXE)) throw new HostProcessError(`No host build at ${HOST_EXE}: run npm run build:host`)
	const port = String(nextPort++)
	const errors = join(await mkdtemp(join(tmpdir(), 'zing-host-e2e-')), 'errors.log')
	const host: ChildProcess = spawn(HOST_EXE, [], {
		env: { ...process.env, ZING_E2E: '1', ZING_CDP_PORT: port, ZING_ERRORS_FILE: errors, ZING_RENDERER_DIR: join(ROOT, 'out', 'renderer'), ...env },
		stdio: 'ignore',
	})
	await expect
		.poll(async () => (await fetch(`http://127.0.0.1:${port}/json/version`).catch(() => undefined))?.ok ?? false, { timeout: 30_000 })
		.toBe(true)
	const browser = await chromium.connectOverCDP(`http://127.0.0.1:${port}`)
	const chrome = await pageWhere(browser, (p) => p.url() === CHROME_URL)
	await chrome.waitForLoadState('domcontentloaded')
	return {
		browser,
		chrome,
		close: async () => {
			await browser.close().catch(() => undefined)
			host.kill()
			if (existsSync(errors)) throw new HostProcessError(`The host recorded errors during this test:\n\n${readFileSync(errors, 'utf8')}`)
		},
	}
}

export function pages(browser: Browser): Page[] {
	return browser.contexts().flatMap((context) => context.pages())
}

export async function pageWhere(browser: Browser, match: (page: Page) => boolean): Promise<Page> {
	await expect.poll(() => pages(browser).some(match), { timeout: 20_000 }).toBe(true)
	return pages(browser).find(match)!
}
