// W-2's guard (docs/architecture-review.md): a window that closes at any await of its startup
// leaves nothing behind and records no error. Closing the only window quits Zing, so each run
// closes a second window (New Window) and the first one checks the counts.
import { createServer, type Server } from 'node:http'
import type { AddressInfo } from 'node:net'
import { expect, type Browser, type Page } from '@playwright/test'
import type { ZingApi } from '../src/shared/ipc'
import { launchZing, test, layers, liveCounts, pages, pageWhere, peekState, type LiveCounts } from './zing'

declare const window: { readonly zing: ZingApi }

const run = (chrome: Page, id: string) => chrome.evaluate((command) => window.zing.runCommand(command as never), id)

const isChrome = (page: Page) => page.url().endsWith('#chrome')

/** The next window closes itself at its n-th scope checkpoint (TestHooks "close-at"). */
const closeNextWindowAt = (chrome: Page, n: number) =>
	chrome.evaluate((at) => (globalThis as unknown as { chrome: { webview: { postMessage(m: unknown): void } } }).chrome.webview.postMessage({ kind: 'test', action: 'close-at', n: at }), n)

/** Polls the host's live counts until they match, and returns them. */
async function countsMatching(chrome: Page, expected: Record<string, unknown>, message: string): Promise<LiveCounts> {
	let counts: LiveCounts | undefined
	await expect
		.poll(
			async () => {
				counts = await liveCounts(chrome)
				return counts
			},
			{ message, timeout: 30_000 },
		)
		.toMatchObject(expected)
	return counts!
}

test('a window that closes at any checkpoint of its startup leaves nothing behind', async () => {
	test.setTimeout(300_000)
	const zing = await launchZing()
	const first = zing.chrome
	// The first window at rest: started, and no view still being created.
	const rest = await countsMatching(first, { windows: 1, pending: 0, newest: { serial: 1, started: true } }, 'the first window starts')

	// A second window started undisturbed passes every checkpoint of a startup.
	await run(first, 'window.new')
	const opened = await countsMatching(first, { windows: 2, pending: 0, newest: { serial: 2, started: true, closed: false } }, 'a second window starts')
	const checkpoints = opened.newest.checkpoints
	expect(checkpoints).toBeGreaterThanOrEqual(3)
	await expect.poll(() => pages(zing.browser).filter(isChrome).length).toBe(2)
	const second = pages(zing.browser).filter(isChrome).find((p) => p !== first)!
	// Listed once its document commits, before its bundle has put zing on window; that has run by DOMContentLoaded.
	await second.waitForLoadState('domcontentloaded')
	// Its renderer goes with the window, so the call may never answer.
	void second.evaluate(() => window.zing.runCommand('window.close')).catch(() => undefined)
	await countsMatching(first, { windows: 1, pending: 0, views: rest.views, newest: { serial: 2, closed: true } }, 'the second window closes')

	for (let n = 1; n <= checkpoints; n++) {
		const serial = 2 + n
		await closeNextWindowAt(first, n)
		await run(first, 'window.new')
		// It closed itself at checkpoint n, and everything it started went with it.
		const after = await countsMatching(first, { windows: 1, pending: 0, views: rest.views, newest: { serial, closed: true } }, `a window closed at checkpoint ${n} of ${checkpoints} leaves nothing behind`)
		expect(after.newest.checkpoints).toBeGreaterThanOrEqual(n)
	}

	// Zing still works: the first window answers, and a new window starts as usual.
	expect((await first.evaluate(() => window.zing.getTabs())).tabs.length).toBeGreaterThan(0)
	await run(first, 'window.new')
	await countsMatching(first, { windows: 2, pending: 0, newest: { serial: 3 + checkpoints, started: true, closed: false } }, 'a window after the teardowns starts')
	await zing.close()
})

// W-2's guard over a link preview starting (docs/superpowers/specs/2026-11-08-link-preview-design.md): its page and
// its buttons' view are views of the window, so the window can close at any await of their start.
let server: Server
let base = ''

test.beforeAll(async () => {
	server = createServer((req, res) => {
		res.setHeader('content-type', 'text/html')
		const port = (server.address() as AddressInfo).port
		if ((req.url ?? '').startsWith('/a')) return void res.end(`<!doctype html><title>Page a</title><a id="cross" href="http://localhost:${port}/b">cross</a>`)
		res.end('<!doctype html><title>Page b</title><p>b</p>')
	})
	await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve))
	base = `http://127.0.0.1:${(server.address() as AddressInfo).port}`
})

test.afterAll(() => new Promise<void>((resolve) => server.close(() => resolve())))

/** The chrome of the window that isn't the first, once its bundle has put zing on window. */
async function otherChrome(browser: Browser, first: Page): Promise<Page> {
	await expect.poll(() => pages(browser).filter((p) => isChrome(p) && p !== first).length).toBe(1)
	const other = pages(browser).find((p) => isChrome(p) && p !== first)!
	await other.waitForLoadState('domcontentloaded')
	return other
}

/** The window's tab on a page whose link goes to another site, loaded; the address is the window's own, so its page can be told apart. */
async function openSource(browser: Browser, chrome: Page, serial: number): Promise<Page> {
	const url = `${base}/a?window=${serial}`
	await chrome.evaluate(async (address) => {
		const { activeId } = await window.zing.getTabs()
		await window.zing.navigateTab(activeId!, address)
	}, url)
	const source = await pageWhere(browser, (p) => p.url() === url)
	await source.waitForLoadState('load')
	return source
}

test('a window that closes at any checkpoint of a link preview starting leaves nothing behind', async () => {
	test.setTimeout(300_000)
	const zing = await launchZing()
	const first = zing.chrome
	const rest = await countsMatching(first, { windows: 1, pending: 0, newest: { serial: 1, started: true } }, 'the first window starts')

	// A preview started undisturbed in a second window: the checkpoints it passes are the ones to close at.
	await run(first, 'window.new')
	await countsMatching(first, { windows: 2, pending: 0, newest: { serial: 2, started: true, closed: false } }, 'a second window starts')
	let second = await otherChrome(zing.browser, first)
	let source = await openSource(zing.browser, second, 2)
	const before = (await countsMatching(first, { windows: 2, pending: 0 }, 'the source page loads')).newest.checkpoints
	await source.click('#cross')
	await expect.poll(async () => (await peekState(second)).open).toBe(true)
	await expect.poll(async () => (await layers(second)).some((l) => l.url.endsWith('#peek') && l.visible)).toBe(true)
	const after = (await countsMatching(first, { windows: 2, pending: 0 }, 'the preview starts')).newest.checkpoints
	expect(after).toBeGreaterThan(before)
	void second.evaluate(() => window.zing.runCommand('window.close')).catch(() => undefined)
	await countsMatching(first, { windows: 1, pending: 0, views: rest.views, newest: { serial: 2, closed: true } }, 'the window with the preview closes')

	for (let n = before + 1; n <= after; n++) {
		const serial = 2 + n - before
		await closeNextWindowAt(first, n)
		await run(first, 'window.new')
		await countsMatching(first, { windows: 2, pending: 0, newest: { serial, started: true, closed: false } }, `window ${serial} starts`)
		second = await otherChrome(zing.browser, first)
		source = await openSource(zing.browser, second, serial)
		// The preview's checkpoints are still to come, so the close lands in its start.
		expect((await countsMatching(first, { windows: 2, pending: 0 }, `window ${serial} loads its page`)).newest.checkpoints).toBe(before)
		// The window can close while the click is still being answered.
		await source.click('#cross').catch(() => undefined)
		await countsMatching(first, { windows: 1, pending: 0, views: rest.views, newest: { serial, closed: true } }, `a window closed at checkpoint ${n - before} of the preview's ${after - before} leaves nothing behind`)
	}

	expect((await first.evaluate(() => window.zing.getTabs())).tabs.length).toBeGreaterThan(0)
	await zing.close()
})
