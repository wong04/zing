import { spawn, type ChildProcess } from 'node:child_process'
import { createServer } from 'node:net'
import type { AddressInfo } from 'node:net'
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
	/** Where this run kept settings and the library; pass it to the next launch to test a relaunch. */
	readonly userDataDir: string
	/** Starts Zing again on the same profile, as a link click from another app would, and resolves with its exit code. */
	launchAgain(args: readonly string[]): Promise<number | null>
	close(): Promise<void>
}

export interface LaunchOptions {
	readonly env?: Readonly<Record<string, string>>
	readonly userDataDir?: string
	readonly args?: readonly string[]
}

/**
 * A port nothing listens on now. A fixed counter restarted at the same number in a fresh worker
 * after a crash, and the next test attached to the crashed run's host, which was still running.
 */
async function freePort(): Promise<string> {
	const probe = createServer()
	await new Promise<void>((resolve) => probe.listen(0, '127.0.0.1', resolve))
	const { port } = probe.address() as AddressInfo
	await new Promise<void>((resolve) => probe.close(() => resolve()))
	return String(port)
}

/**
 * Starts Zing's WebView2 host off-screen (ZING_E2E) and attaches over CDP.
 * `close()` fails the test if the host recorded any unhandled error, the
 * WebView2 counterpart of e2e/zing.ts's main-process guard.
 */
export async function launchZing({ env = {}, userDataDir, args = [] }: LaunchOptions = {}): Promise<Launched> {
	if (!existsSync(HOST_EXE)) throw new HostProcessError(`No host build at ${HOST_EXE}: run npm run build:host`)
	const port = await freePort()
	const errors = join(await mkdtemp(join(tmpdir(), 'zing-host-e2e-')), 'errors.log')
	// Never the user's real profile: every run gets its own folder unless a test relaunches into one.
	const userData = userDataDir ?? (await mkdtemp(join(tmpdir(), 'zing-host-data-')))
	const hostEnv = {
		...process.env,
		ZING_E2E: '1',
		ZING_CDP_PORT: port,
		ZING_ERRORS_FILE: errors,
		ZING_RENDERER_DIR: join(ROOT, 'out', 'renderer'),
		ZING_USER_DATA: userData,
		ZING_DOWNLOADS_DIR: join(userData, 'downloads'),
		ZING_SKIP_ACCOUNT_GATE: '1',
		...env,
	}
	const host: ChildProcess = spawn(HOST_EXE, [...args], { env: hostEnv, stdio: 'ignore' })
	await expect
		.poll(async () => (await fetch(`http://127.0.0.1:${port}/json/version`).catch(() => undefined))?.ok ?? false, { timeout: 30_000 })
		.toBe(true)
	const browser = await chromium.connectOverCDP(`http://127.0.0.1:${port}`)
	const chrome = await pageWhere(browser, (p) => p.url() === CHROME_URL)
	await chrome.waitForLoadState('domcontentloaded')
	return {
		browser,
		chrome,
		userDataDir: userData,
		launchAgain: (again) =>
			new Promise((resolve, reject) => {
				const second = spawn(HOST_EXE, [...again], { env: hostEnv, stdio: 'ignore' })
				second.once('error', reject)
				second.once('exit', (code) => resolve(code))
			}),
		close: async () => {
			// Quit as a person would, by closing every window, so pending writes are flushed.
			const exited = new Promise<void>((resolve) => host.once('exit', () => resolve()))
			const chromes = pages(browser).filter((p) => p.url() === CHROME_URL)
			// One window at a time, as a person closes them. A window that closes takes its renderer
			// with it, so the evaluate may never answer; the page's close event is the answer.
			for (const page of chromes) {
				const gone = new Promise<void>((resolve) => page.once('close', () => resolve()))
				void page.evaluate(() => (globalThis as unknown as { zing: { runCommand(id: string): Promise<unknown> } }).zing.runCommand('window.close')).catch(() => undefined)
				await Promise.race([gone, exited, new Promise((resolve) => setTimeout(resolve, 5_000))])
			}
			const quit = await Promise.race([exited.then(() => true), new Promise<boolean>((resolve) => setTimeout(() => resolve(false), 15_000))])
			// An exited host has already dropped the DevTools connection; closing it again crashed the
			// Playwright worker (0xC0000409) about once per full run.
			if (browser.isConnected()) await browser.close()
			if (!quit) host.kill()
			if (existsSync(errors)) throw new HostProcessError(`The host recorded errors during this test:\n\n${readFileSync(errors, 'utf8')}`)
			if (!quit) throw new HostProcessError('Zing did not quit within 15 s of its last window closing')
		},
	}
}

export function pages(browser: Browser): Page[] {
	return browser.contexts().flatMap((context) => context.pages())
}

export async function pageWhere(browser: Browser, match: (page: Page) => boolean): Promise<Page> {
	await expect.poll(() => pages(browser).some(match), { timeout: 40_000 }).toBe(true)
	return pages(browser).find(match)!
}
