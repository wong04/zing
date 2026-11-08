// W-2's guard (docs/architecture-review.md): a window that closes at any await of its startup
// leaves nothing behind and records no error. Closing the only window quits Zing, so each run
// closes a second window (New Window) and the first one checks the counts.
import { expect, type Page } from '@playwright/test'
import type { ZingApi } from '../src/shared/ipc'
import { launchZing, test, liveCounts, pages, type LiveCounts } from './zing'

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
