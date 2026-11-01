import { existsSync, readFileSync } from 'node:fs'
import { createServer, type Server } from 'node:http'
import { join } from 'node:path'
import type { AddressInfo } from 'node:net'
import { expect, test, type CDPSession, type Page } from '@playwright/test'
import type { TabView, ZingApi } from '../src/shared/ipc'
import { launchZing, liveCounts, pageWhere } from './zing'

declare const window: { readonly zing: ZingApi }

/*
 * W-4: a page whose renderer dies keeps its tab (a tab is a record), shows that it crashed, and Reload
 * brings it back. A renderer exit is the page's failure, not Zing's, so the host logs it as a warning
 * (EngineFailures.Warn), never in ZING_ERRORS_FILE: these tests would fail on an error.
 *
 * The renderer is killed with DevTools' Page.crash rather than edge://kill, which Chromium honours only
 * for a navigation typed in an address bar. Playwright treats a crashed page as gone for good, so after
 * the crash the tests talk to that target over a raw CDP session, or read the tab through the chrome.
 */

let server: Server
let base = ''
let loads = 0

test.beforeAll(async () => {
	server = createServer((req, res) => {
		res.setHeader('content-type', 'text/html')
		if (req.url === '/crash') {
			loads++
			// Each load says which it is, so a reloaded page can be told from the one that crashed.
			return void res.end(`<!doctype html><title>Crash fixture ${loads}</title><p>Load ${loads}</p>`)
		}
		res.end('<!doctype html><title>Other</title>')
	})
	await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve))
	base = `http://127.0.0.1:${(server.address() as AddressInfo).port}`
})

test.afterAll(() => new Promise<void>((resolve) => server.close(() => resolve())))

test.beforeEach(() => {
	loads = 0
})

async function activeTab(chrome: Page): Promise<TabView | undefined> {
	const state = await chrome.evaluate(() => window.zing.getTabs())
	return state.tabs.find((t) => t.id === state.activeId)
}

/** Kills the target's renderer. The call never answers (the process that would answer is gone), so it isn't awaited. */
function crash(session: CDPSession): void {
	void session.send('Page.crash').catch(() => undefined)
}

test('a crashed page keeps its tab, says so, and Reload brings it back', async () => {
	const zing = await launchZing({ env: { ZING_START_URL: `${base}/crash` } })
	const page = await pageWhere(zing.browser, (p) => p.url() === `${base}/crash`)
	await expect.poll(async () => (await activeTab(zing.chrome))?.title).toBe('Crash fixture 1')
	const before = (await activeTab(zing.chrome))!

	crash(await page.context().newCDPSession(page))

	await expect.poll(async () => (await activeTab(zing.chrome))?.pageState).toBe('crashed')
	const crashed = (await activeTab(zing.chrome))!
	// The same tab, at the same address: only its page went.
	expect(crashed.id).toBe(before.id)
	expect(crashed.url).toBe(`${base}/crash`)
	const chip = zing.chrome.locator(`[role="tab"][data-tab-id="${before.id}"]`)
	await expect(chip).toHaveAttribute('data-page-state', 'crashed')
	await expect(chip).toHaveAttribute('title', /\(crashed\)$/)
	const notice = zing.chrome.locator('.page-fault')
	await expect(notice).toContainText('This page crashed')

	await notice.getByRole('button', { name: 'Reload', exact: true }).click()

	await expect.poll(async () => (await activeTab(zing.chrome))?.title).toBe('Crash fixture 2')
	expect((await activeTab(zing.chrome))?.pageState).toBeUndefined()
	await expect(notice).toHaveCount(0)
	await expect(chip).not.toHaveAttribute('data-page-state', /.+/)
	await zing.close()
})

test('a crashed background tab is marked and comes back when it navigates', async () => {
	const zing = await launchZing({ env: { ZING_START_URL: `${base}/crash` } })
	const page = await pageWhere(zing.browser, (p) => p.url() === `${base}/crash`)
	const { activeId: crashedId } = await zing.chrome.evaluate(() => window.zing.getTabs())
	await zing.chrome.evaluate(() => window.zing.runCommand('tab.new'))

	// This test once lost the chrome page a second after the crash; say exactly what went if it does again.
	const events: string[] = []
	zing.chrome.on('crash', () => events.push('the chrome view crashed'))
	zing.chrome.on('close', () => events.push('the chrome page closed'))
	zing.browser.on('disconnected', () => events.push('the DevTools connection dropped'))
	// The page just left the screen, so its overview picture is being taken; it stays drawn under the new tab
	// until it is (MainWindow.Hide). That capture must finish: a hidden WebView2 never completes one, and the
	// runtime's browser process crashes (0xC0000005) when a renderer dies with a capture of it pending.
	await expect.poll(async () => (await liveCounts(zing.chrome)).captures).toBe(0)
	crash(await page.context().newCDPSession(page))

	try {
		await expect.poll(async () => (await zing.chrome.evaluate(() => window.zing.getTabs())).tabs.find((t) => t.id === crashedId)?.pageState).toBe('crashed')
	} catch (error) {
		let alive = true
		try {
			process.kill(zing.pid, 0)
		} catch {
			alive = false
		}
		const faults = join(zing.userDataDir, 'logs', 'faults.log')
		const log = existsSync(faults) ? readFileSync(faults, 'utf8') : '(no fault log)'
		throw new Error(`${(error as Error).message}\n\nEvents: ${events.join('; ') || 'none'}\nHost alive: ${alive}\nFault log:\n${log}`, { cause: error })
	}
	// Only the active tab's crash shows in the toolbar.
	await expect(zing.chrome.locator('.page-fault')).toHaveCount(0)
	await expect(zing.chrome.locator(`[role="tab"][data-tab-id="${crashedId}"]`)).toHaveAttribute('data-page-state', 'crashed')

	await zing.chrome.evaluate((id) => window.zing.activateTab(id!), crashedId)
	await expect(zing.chrome.locator('.page-fault')).toContainText('This page crashed')
	await zing.chrome.evaluate(([id, address]) => window.zing.navigateTab(id!, address!), [crashedId, `${base}/other`])
	await expect.poll(async () => (await activeTab(zing.chrome))?.title).toBe('Other')
	expect((await activeTab(zing.chrome))?.pageState).toBeUndefined()
	await zing.close()
})

test('Zing’s own chrome view comes back after its renderer dies', async () => {
	const zing = await launchZing()
	const session = await zing.chrome.context().newCDPSession(zing.chrome)
	const probe = async (expression: string): Promise<unknown> => {
		try {
			const answer = (await session.send('Runtime.evaluate', { expression, returnByValue: true })) as { result: { value?: unknown } }
			return answer.result.value
		} catch {
			// The renderer is gone or the document is being replaced.
			return undefined
		}
	}
	await probe('window.__beforeCrash = true')
	expect(await probe('window.__beforeCrash === true')).toBe(true)

	crash(session)

	// A fresh document (the marker is gone) that drew the tab strip from the host's state.
	await expect.poll(() => probe('window.__beforeCrash !== true && document.querySelectorAll(\'.tabs > [role="tab"]\').length'), { timeout: 20_000 }).toBe(1)
	// Playwright gave up on the crashed page, so close() can't reach the window through it: close it over the session as close() starts.
	const closing = zing.close()
	await probe('window.zing.runCommand("window.close")')
	await closing
})
