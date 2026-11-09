import { execFileSync, spawn, type ChildProcess } from 'node:child_process'
import { createServer } from 'node:net'
import type { AddressInfo } from 'node:net'
import { closeSync, copyFileSync, existsSync, mkdtempSync, openSync, readFileSync, realpathSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { test as base, chromium, expect, type Browser, type Page, type TestInfo } from '@playwright/test'

const ROOT = join(import.meta.dirname, '..')
export const HOST_EXE = join(ROOT, 'host', 'Zing.Host', 'bin', 'Release', 'net9.0-windows10.0.26100.0', 'win-x64', 'Zing.exe')
export const BUILT_RENDERER = join(ROOT, 'out', 'renderer')
const CHROME_URL = 'https://app.zing/index.html#chrome'

export class HostProcessError extends Error {
	override name = 'HostProcessError'
}

export interface Launched {
	readonly browser: Browser
	readonly chrome: Page
	/** Where this run kept settings and the library; pass it to the next launch to test a relaunch. */
	readonly userDataDir: string
	/** Zing.exe's process id, for the real-input tier's OS input helper (e2e/real-input.ps1). */
	readonly pid: number
	/** The menus the host opened, oldest first, as their labels (a separator is null); test runs never show them. */
	menus(): (string | null)[][]
	/** The dialogs the host would have opened (Edge's print preview), oldest first; test runs never show them. */
	dialogs(): { readonly kind: string; readonly url: string }[]
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
/** A Zing this test started and hasn't closed yet; on a failure its views and logs go into the report. */
interface Running {
	readonly host: ChildProcess
	/** Made when the host is spawned, so an exit before close() is still seen; the exit code, or null after a signal. */
	readonly exit: Promise<number | null>
	/** None on a launchBare run. */
	readonly browser?: Browser
	readonly errors: string
	readonly userData: string
}

function exitOf(host: ChildProcess): Promise<number | null> {
	return new Promise((resolve) => host.once('exit', (code) => resolve(code)))
}

function describeExit(code: number | null): string {
	return code === null ? 'a signal' : `exit code ${code} (0x${(code >>> 0).toString(16).toUpperCase()})`
}

const QUIT_TIMEOUT = 15_000

/**
 * The end of close(), once every window was asked to close: the host must exit, with code 0 (a native crash during
 * quit, 0xC0000409 or an access violation, writes nothing to ZING_ERRORS_FILE), having recorded no error. The errors
 * are read first, so a lock the engine keeps can't hide them.
 */
async function quitCleanly(self: Running): Promise<void> {
	const code = await Promise.race([self.exit, new Promise<'timeout'>((resolve) => setTimeout(() => resolve('timeout'), QUIT_TIMEOUT))])
	if (code === 'timeout') self.host.kill()
	const recorded = existsSync(self.errors) ? readFileSync(self.errors, 'utf8') : undefined
	// Released before any error is thrown: a test that expects close() to reject still has its profile deleted after it.
	const release = await engineReleased(self.userData).then(
		() => undefined,
		(error: unknown) => error,
	)
	if (recorded !== undefined) throw new HostProcessError(`The host recorded errors during this test:\n\n${recorded}\n${hostLogs(undefined, self.userData)}${release ? `\n${String(release)}` : ''}`)
	if (release) throw release
	if (code === 'timeout') throw new HostProcessError(`Zing did not quit within ${QUIT_TIMEOUT / 1000} s of its last window closing`)
	if (code !== 0) throw new HostProcessError(`Zing quit with ${describeExit(code)}\n${hostLogs(undefined, self.userData)}`)
}

const running = new Set<Running>()

/** The scratch folders this test made: removed when it passes, kept for a look when it fails. */
const scratchDirs = new Set<string>()

/** A new empty folder under the temp folder for this test, removed after it unless it fails. */
export function scratchDir(prefix: string): string {
	// The long form: a CI runner's temp folder is an 8.3 path (RUNNER~1), and the host reports the paths it hands out in full.
	const dir = realpathSync.native(mkdtempSync(join(tmpdir(), prefix)))
	scratchDirs.add(dir)
	return dir
}

/**
 * Every spec's `test`: Playwright's, plus an automatic fixture that, when a test fails, attaches a
 * picture of each Zing view and the host's recorded errors and fault log (CI uploads them as an
 * artifact), then stops any Zing the test left running, so the next test starts clean.
 */
export const test = base.extend<{ zingArtifacts: void }>({
	zingArtifacts: [
		// Playwright reads a fixture's dependencies from this destructuring, so it must be a pattern, even an empty one.
		// eslint-disable-next-line no-empty-pattern
		async ({}, use, testInfo) => {
			await use()
			await collect(testInfo)
		},
		{ auto: true },
	],
})

async function collect(testInfo: TestInfo): Promise<void> {
	const left = [...running]
	running.clear()
	const failed = testInfo.status !== testInfo.expectedStatus
	// A passing test that never called close() skipped its error check; it fails, with what the host recorded. A host
	// already gone is left out: a test that kills it on purpose (a crash) can't close it.
	const unclosed = failed ? [] : left.filter((zing) => zing.host.exitCode === null && zing.host.signalCode === null).map((zing) => (existsSync(zing.errors) ? readFileSync(zing.errors, 'utf8') : '(no errors recorded)'))
	for (const [n, zing] of left.entries()) {
		if (failed) {
			for (const [i, page] of (zing.browser ? pages(zing.browser) : []).entries()) {
				const name = `zing${n + 1}-view${i + 1}-${page.url().replace(/^.*[#/]/, '') || 'blank'}.png`
				// Written into the test's own folder: an attachment's body alone stays in the report, and CI uploads the folder.
				const path = testInfo.outputPath(name)
				if (await page.screenshot({ path, timeout: 5_000 }).then(() => true, () => false)) await testInfo.attach(name, { path, contentType: 'image/png' })
			}
			for (const [log, from] of [[`zing${n + 1}-errors.log`, zing.errors], [`zing${n + 1}-faults.log`, join(zing.userData, 'logs', 'faults.log')]] as const) {
				if (!existsSync(from)) continue
				const path = testInfo.outputPath(log)
				copyFileSync(from, path)
				await testInfo.attach(log, { path, contentType: 'text/plain' })
			}
		}
		if (zing.browser?.isConnected()) await zing.browser.close().catch(() => undefined)
		if (zing.host.exitCode === null) {
			zing.host.kill()
			await zing.exit
		}
		// A host killed on purpose leaves its engine to notice and exit on its own; its profile is deleted below.
		if (!failed) await engineReleased(zing.userData)
	}
	const made = [...scratchDirs]
	scratchDirs.clear()
	if (failed) return
	if (unclosed.length > 0) throw new HostProcessError(`The test left ${unclosed.length} Zing without zing.close(), so its errors were never checked:\n\n${unclosed.join('\n\n')}`)
	for (const dir of made) await removeScratch(dir)
}

/**
 * The engine's lockfile goes with its browser process, but after a page crash its crash reporter (msedgewebview2
 * --type=crashpad-handler) stays a while longer, holding the profile's Crashpad folder. A folder that can't be removed
 * waits until no engine process names it, which is slow to ask, so only then.
 */
async function removeScratch(dir: string): Promise<void> {
	try {
		rmSync(dir, { recursive: true, force: true })
		return
	} catch (error) {
		const code = (error as NodeJS.ErrnoException).code
		if (code !== 'EPERM' && code !== 'EBUSY') throw error
	}
	await engineGone(dir)
	rmSync(dir, { recursive: true, force: true, maxRetries: 10, retryDelay: 200 })
}

// A cold start on a CI runner has taken 29 s and more, while a warm one takes under 5.
const LAUNCH_TIMEOUT = 60_000

/** What the harness sets itself: a test can't change them, and the shell's own values never reach the host. */
const PROTECTED = ['ZING_E2E', 'ZING_CDP_PORT', 'ZING_ERRORS_FILE', 'ZING_USER_DATA', 'ZING_MENUS_FILE', 'ZING_DIALOGS_FILE']

/** The shell's environment without its ZING_* variables: a stray one (ZING_START_URL, ZING_USER_DATA) would change the run. */
function inheritedEnvironment(): NodeJS.ProcessEnv {
	return Object.fromEntries(Object.entries(process.env).filter(([key]) => !key.toUpperCase().startsWith('ZING_')))
}

function hostEnvironment(port: string, errors: string, userData: string): NodeJS.ProcessEnv {
	return {
		...inheritedEnvironment(),
		ZING_E2E: '1',
		ZING_CDP_PORT: port,
		ZING_ERRORS_FILE: errors,
		ZING_RENDERER_DIR: BUILT_RENDERER,
		ZING_USER_DATA: userData,
		ZING_DOWNLOADS_DIR: join(userData, 'downloads'),
		ZING_SKIP_ACCOUNT_GATE: '1',
	}
}

export async function launchZing({ env = {}, userDataDir, args = [] }: LaunchOptions = {}): Promise<Launched> {
	if (!existsSync(HOST_EXE)) throw new HostProcessError(`No host build at ${HOST_EXE}: run npm run build:host`)
	const overridden = Object.keys(env).filter((key) => PROTECTED.includes(key.toUpperCase()))
	if (overridden.length > 0) throw new HostProcessError(`launchZing sets ${overridden.join(', ')} itself; a test can't override it`)
	const port = await freePort()
	const scratch = scratchDir('zing-host-e2e-')
	const errors = join(scratch, 'errors.log')
	const menus = join(scratch, 'menus.jsonl')
	const dialogs = join(scratch, 'dialogs.jsonl')
	// Never the user's real profile: every run gets its own folder unless a test relaunches into one.
	const userData = userDataDir === undefined ? scratchDir('zing-host-data-') : realpathSync.native(userDataDir)
	const hostEnv = { ...hostEnvironment(port, errors, userData), ZING_MENUS_FILE: menus, ZING_DIALOGS_FILE: dialogs, ...env }
	const host: ChildProcess = spawn(HOST_EXE, [...args], { env: hostEnv, stdio: 'ignore' })
	const exit = exitOf(host)
	let closing = false
	// A host that goes before close() leaves the test only "target closed"; say how it went and what it logged.
	void exit.then((code) => {
		if (closing) return
		process.stderr.write(`Zing exited before close(), with ${describeExit(code)}.\n${hostLogs(errors, userData)}\n`)
	})
	await expect
		.poll(async () => (await fetch(`http://127.0.0.1:${port}/json/version`).catch(() => undefined))?.ok ?? false, { timeout: LAUNCH_TIMEOUT })
		.toBe(true)
	const browser = await chromium.connectOverCDP(`http://127.0.0.1:${port}`)
	const self: Running = { host, exit, browser, errors, userData }
	running.add(self)
	const chrome = await pageWhere(browser, (p) => p.url() === CHROME_URL)
	await chrome.waitForLoadState('domcontentloaded')
	// Loaded isn't started: on a cold runner the host's first answer and the chrome's first frame, where it applies host
	// state (useTabsState), have come more than 15 s later, past a test's first expect. Launching is allowed the cold start.
	await chrome.waitForFunction(
		async () => {
			await (globalThis as unknown as { zing: { getTabs(): Promise<unknown> } }).zing.getTabs()
			return new Promise<boolean>((resolve) => requestAnimationFrame(() => resolve(true)))
		},
		undefined,
		{ timeout: LAUNCH_TIMEOUT },
	)
	return {
		browser,
		chrome,
		userDataDir: userData,
		pid: host.pid!,
		menus: () =>
			existsSync(menus)
				? readFileSync(menus, 'utf8')
						.split('\n')
						.filter((line) => line.length > 0)
						.map((line) => JSON.parse(line) as (string | null)[])
				: [],
		dialogs: () =>
			existsSync(dialogs)
				? readFileSync(dialogs, 'utf8')
						.split('\n')
						.filter((line) => line.length > 0)
						.map((line) => JSON.parse(line) as { kind: string; url: string })
				: [],
		launchAgain: (again) =>
			new Promise((resolve, reject) => {
				const second = spawn(HOST_EXE, [...again], { env: hostEnv, stdio: 'ignore' })
				second.once('error', reject)
				second.once('exit', (code) => resolve(code))
			}),
		close: async () => {
			closing = true
			running.delete(self)
			// Quit as a person would, by closing every window, so pending writes are flushed.
			const exited = exit.then(() => undefined)
			const chromes = pages(browser).filter((p) => p.url() === CHROME_URL)
			// One window at a time, as a person closes them. A window that closes takes its renderer
			// with it, so the evaluate may never answer; the page's close event is the answer.
			for (const page of chromes) {
				const gone = new Promise<void>((resolve) => page.once('close', () => resolve()))
				void page.evaluate(() => (globalThis as unknown as { zing: { runCommand(id: string): Promise<unknown> } }).zing.runCommand('window.close')).catch(() => undefined)
				await Promise.race([gone, exited, new Promise((resolve) => setTimeout(resolve, 5_000))])
			}
			await Promise.race([exited, new Promise((resolve) => setTimeout(resolve, QUIT_TIMEOUT))])
			// An exited host has already dropped the DevTools connection; closing it again crashed the
			// Playwright worker (0xC0000409) about once per full run.
			if (browser.isConnected()) await browser.close()
			await quitCleanly(self)
		},
	}
}

export interface BareZing {
	/** The URLs of every view, in no order. */
	urls(): Promise<string[]>
	/** Evaluates an expression in every view whose URL matches, in no order. */
	evaluate(match: (url: string) => boolean, expression: string): Promise<unknown[]>
	close(): Promise<void>
}

/**
 * Zing with no Playwright attached, read over plain DevTools connections. Playwright emulates a colour
 * scheme on every view it attaches to, and clearing that also clears the host's own override, so what a
 * view gets from the host when it starts can only be seen without it.
 */
export async function launchBare(userDataDir: string): Promise<BareZing> {
	const port = await freePort()
	const errors = join(scratchDir('zing-host-e2e-'), 'errors.log')
	const host = spawn(HOST_EXE, [], { env: hostEnvironment(port, errors, userDataDir), stdio: 'ignore' })
	const self: Running = { host, exit: exitOf(host), errors, userData: userDataDir }
	running.add(self)
	const targets = async () => {
		const response = await fetch(`http://127.0.0.1:${port}/json/list`)
		const all = (await response.json()) as { type: string; url: string; webSocketDebuggerUrl: string }[]
		return all.filter((target) => target.type === 'page')
	}
	const bare: BareZing = {
		urls: async () => (await targets()).map((target) => target.url),
		evaluate: async (match, expression) => Promise.all((await targets()).filter((target) => match(target.url)).map((target) => evaluateOver(target.webSocketDebuggerUrl, expression))),
		close: async () => {
			running.delete(self)
			// The window closing takes its view with it, so this may never answer; the host exiting is the answer.
			void bare.evaluate((url) => url === CHROME_URL, "zing.runCommand('window.close')").catch(() => undefined)
			await quitCleanly(self)
		},
	}
	await expect
		.poll(async () => (await targets().catch(() => [])).some((target) => target.url === CHROME_URL), { timeout: LAUNCH_TIMEOUT })
		.toBe(true)
	return bare
}

/** One Runtime.evaluate on a throwaway connection; a view that closes meanwhile fails it. */
function evaluateOver(webSocketUrl: string, expression: string): Promise<unknown> {
	return new Promise((resolve, reject) => {
		const socket = new WebSocket(webSocketUrl)
		socket.onopen = () => socket.send(JSON.stringify({ id: 1, method: 'Runtime.evaluate', params: { expression, awaitPromise: true, returnByValue: true } }))
		socket.onerror = () => reject(new HostProcessError(`No DevTools connection to ${webSocketUrl}`))
		socket.onclose = () => reject(new HostProcessError(`The view at ${webSocketUrl} closed during an evaluate`))
		socket.onmessage = (message) => {
			const { result } = JSON.parse(String(message.data)) as { result: { result: { value: unknown }; exceptionDetails?: unknown } }
			if (result.exceptionDetails) reject(new HostProcessError(`${expression} threw: ${JSON.stringify(result.exceptionDetails)}`))
			else resolve(result.result.value)
			socket.close()
		}
	})
}

/** The e2e run's own logs: the errors file (when given) and the scratch profile's fault log, warnings included. */
function hostLogs(errors: string | undefined, userData: string): string {
	const parts: string[] = []
	if (errors && existsSync(errors)) parts.push(`Recorded errors:\n${readFileSync(errors, 'utf8')}`)
	const faults = join(userData, 'logs', 'faults.log')
	if (existsSync(faults)) parts.push(`Fault log (${faults}):\n${readFileSync(faults, 'utf8')}`)
	return parts.length > 0 ? parts.join('\n') : 'The host logged nothing.'
}

const ENGINE_RELEASE_TIMEOUT = 30_000
const SLOW_RELEASE = 2_000

/** A release that took long but succeeded still shows how close CI runs to the limit. */
function reportSlowRelease(started: number): void {
	const took = Date.now() - started
	if (took > SLOW_RELEASE) process.stderr.write(`The WebView2 engine took ${took} ms to release its profile.
`)
}

/**
 * Every process that still has this profile as its --user-data-dir, with its parent and age, so a
 * stalled release shows what is alive (a browser process, a crashpad handler, a leftover renderer) and who owns it.
 */
function engineProcesses(userData: string): string {
	try {
		const out = listEngineProcesses(userData)
		return out.length > 0 ? `Processes using ${userData}:
${out}` : `No process has ${userData} on its command line.`
	} catch (error) {
		return `Could not list the engine's processes: ${(error as Error).message}`
	}
}

/** One line per engine process with this folder on its command line; empty when there is none. */
function listEngineProcesses(userData: string): string {
	const script = `$d = '${userData.replaceAll("'", "''")}'; Get-CimInstance Win32_Process -Filter "Name='msedgewebview2.exe'" | Where-Object { $_.CommandLine -and $_.CommandLine.Contains($d) } | Sort-Object CreationDate | ForEach-Object { '{0} parent={1} started={2:HH:mm:ss.fff} {3}' -f $_.ProcessId, $_.ParentProcessId, $_.CreationDate, $_.CommandLine.Substring(0, [Math]::Min(220, $_.CommandLine.Length)) }`
	return execFileSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command', script], { encoding: 'utf8', timeout: 20_000 }).trim()
}

/** Waits until no engine process names this folder. */
async function engineGone(userData: string): Promise<void> {
	const deadline = Date.now() + ENGINE_RELEASE_TIMEOUT
	for (;;) {
		const left = listEngineProcesses(userData)
		if (left.length === 0) return
		if (Date.now() > deadline) throw new HostProcessError(`The WebView2 engine still uses ${userData} ${ENGINE_RELEASE_TIMEOUT / 1000} s after Zing quit:\n${left}`)
		await new Promise((resolve) => setTimeout(resolve, 250))
	}
}

/**
 * The engine's processes outlive Zing.exe for a moment and keep the profile locked; a test that
 * reads the profile after close() (a relaunch, a scan for secrets) must wait for them.
 */
async function engineReleased(userData: string): Promise<void> {
	const lockfile = join(userData, 'WebView2', 'EBWebView', 'lockfile')
	const started = Date.now()
	const deadline = started + ENGINE_RELEASE_TIMEOUT
	for (;;) {
		try {
			closeSync(openSync(lockfile, 'r+'))
			reportSlowRelease(started)
			return
		} catch (error) {
			const code = (error as NodeJS.ErrnoException).code
			if (code === 'ENOENT') return reportSlowRelease(started)
			if (code !== 'EBUSY' && code !== 'EPERM') throw error
			if (Date.now() > deadline) throw new HostProcessError(`The WebView2 engine still holds ${lockfile} ${ENGINE_RELEASE_TIMEOUT / 1000} s after Zing quit
${engineProcesses(userData)}`)
			await new Promise((resolve) => setTimeout(resolve, 100))
		}
	}
}

export interface Layer {
	readonly url: string
	readonly visible: boolean
	/** Where the view sits in the window's content, in DIPs. */
	readonly x: number
	readonly y: number
	readonly width: number
	readonly height: number
	/** How many sibling windows are stacked above the view's window (0 is the top); compare views of the same kind. */
	readonly depth: number
	/** Whether the window's keyboard focus is in this view (Win32 focus on the window's thread, which an off-screen window keeps too). */
	readonly keyboard: boolean
}

/** What the window really shows, bottom to top (TestHooks "layers"); page-side visibility doesn't follow a hidden view. */
export async function layers(chrome: Page): Promise<Layer[]> {
	const file = join(scratchDir('zing-layers-'), 'layers.json')
	await chrome.evaluate((path) => (globalThis as unknown as { chrome: { webview: { postMessage(m: unknown): void } } }).chrome.webview.postMessage({ kind: 'test', action: 'layers', file: path }), file)
	await expect.poll(() => existsSync(file)).toBe(true)
	return JSON.parse(readFileSync(file, 'utf8')) as Layer[]
}

export interface PeekState {
	readonly open: boolean
	/** The preview's pages, bottom first: its page, then any popups stacked over it. */
	readonly layers: readonly { readonly url: string }[]
	readonly canExpand: boolean
	readonly sourceTabId: string | null
}

/** The link preview open in this window, if any (TestHooks "peek-state"). */
export async function peekState(chrome: Page): Promise<PeekState> {
	const file = join(scratchDir('zing-peek-'), 'peek.json')
	await chrome.evaluate((path) => (globalThis as unknown as { chrome: { webview: { postMessage(m: unknown): void } } }).chrome.webview.postMessage({ kind: 'test', action: 'peek-state', file: path }), file)
	await expect.poll(() => existsSync(file)).toBe(true)
	return JSON.parse(readFileSync(file, 'utf8')) as PeekState
}

/** A left-button press on the preview's dim, as a click there would send (TestHooks "click-scrim"). */
export async function clickScrim(chrome: Page): Promise<void> {
	await chrome.evaluate(async () => {
		const view = globalThis as unknown as { chrome: { webview: { postMessage(m: unknown): void } }; zing: { getTabs(): Promise<unknown> } }
		view.chrome.webview.postMessage({ kind: 'test', action: 'click-scrim' })
		// Answered after the message before it on the same channel: the press has been sent.
		await view.zing.getTabs()
	})
}

/**
 * A key pressed on the link preview's top page, through the window's key handler as the engine's AcceleratorKeyPressed
 * would enter it (TestHooks "preview-key"): CDP input never reaches that event.
 */
export async function pressInPreview(chrome: Page, code: 'Escape'): Promise<void> {
	await chrome.evaluate(async (c) => {
		const view = globalThis as unknown as { chrome: { webview: { postMessage(m: unknown): void } }; zing: { getTabs(): Promise<unknown> } }
		view.chrome.webview.postMessage({ kind: 'test', action: 'preview-key', code: c })
		// Answered after the message before it on the same channel: the key has been handled.
		await view.zing.getTabs()
	}, code)
}

export interface Modifiers {
	readonly shift?: boolean
	readonly ctrl?: boolean
	readonly middle?: boolean
}

/** The keys and button the next new-window request is taken to have been made with, once (TestHooks "set-modifiers"); e2e runs never read the real ones. */
export async function setModifiers(chrome: Page, { shift = false, ctrl = false, middle = false }: Modifiers): Promise<void> {
	await chrome.evaluate(async (m) => {
		const view = globalThis as unknown as { chrome: { webview: { postMessage(m: unknown): void } }; zing: { getTabs(): Promise<unknown> } }
		view.chrome.webview.postMessage({ kind: 'test', action: 'set-modifiers', ...m })
		// Answered after the message before it on the same channel: the modifiers are set before the click that reads them.
		await view.zing.getTabs()
	}, { shift, ctrl, middle })
}

/** The colours the host gave Windows' caption buttons, as channels, and their height in DIPs. */
export async function captionColours(chrome: Page): Promise<CaptionState> {
	const file = join(scratchDir('zing-caption-'), 'caption.json')
	await chrome.evaluate((path) => (globalThis as unknown as { chrome: { webview: { postMessage(m: unknown): void } } }).chrome.webview.postMessage({ kind: 'test', action: 'caption', file: path }), file)
	await expect.poll(() => existsSync(file)).toBe(true)
	return JSON.parse(readFileSync(file, 'utf8')) as CaptionState
}

export interface CaptionState {
	readonly background: Channels | null
	readonly ink: Channels | null
	readonly height: number
	/** The buttons' size in pixels as Windows has it now, and as the window last left them out of the pass-through band. */
	readonly buttonsHeight: number
	readonly buttonsWidth: number
	readonly passedBand: number
	readonly passedButtons: number
}

export interface Channels {
	readonly R: number
	readonly G: number
	readonly B: number
}

export interface LiveCounts {
	/** Zing windows not yet destroyed. */
	readonly windows: number
	/** WebView2 views not yet closed, in every window. */
	readonly views: number
	/** Views whose controller the engine is still creating. */
	readonly pending: number
	/** Page captures (CapturePreviewAsync) the engine hasn't finished, e.g. the picture taken as a tab leaves the screen. */
	readonly captures: number
	/** The newest window: how many windows came before it and itself, its scope checkpoints so far, whether it finished starting and whether it closed. */
	readonly newest: { readonly serial: number; readonly checkpoints: number; readonly started: boolean; readonly closed: boolean }
}

/** What is still alive in the host (TestHooks "live-counts"), for teardown tests. */
export async function liveCounts(chrome: Page): Promise<LiveCounts> {
	const file = join(scratchDir('zing-live-'), 'live.json')
	await chrome.evaluate((path) => (globalThis as unknown as { chrome: { webview: { postMessage(m: unknown): void } } }).chrome.webview.postMessage({ kind: 'test', action: 'live-counts', file: path }), file)
	let counts: LiveCounts | undefined
	// Read until whole: the host may still be writing the file when it first appears.
	await expect
		.poll(() => {
			try {
				counts = JSON.parse(readFileSync(file, 'utf8')) as LiveCounts
				return true
			} catch {
				return false
			}
		})
		.toBe(true)
	return counts!
}

export function pages(browser: Browser): Page[] {
	return browser.contexts().flatMap((context) => context.pages())
}

export async function pageWhere(browser: Browser, match: (page: Page) => boolean): Promise<Page> {
	await expect.poll(() => pages(browser).some(match), { timeout: 40_000 }).toBe(true)
	return pages(browser).find(match)!
}
