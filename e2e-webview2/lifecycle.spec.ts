import { createServer, type Server } from 'node:http'
import type { AddressInfo } from 'node:net'
import { expect, test, type Page } from '@playwright/test'
import type { ZingApi } from '../src/shared/ipc'
import { launchZing, pages, pageWhere } from './zing'

declare const window: { readonly zing: ZingApi }

type PageGlobal = { scrollTo(x: number, y: number): void; scrollY: number }

const MINUTE = 60_000
const TEST_CLOCK = { ZING_TEST_CLOCK: '1' }

let server: Server
let base = ''

test.beforeAll(async () => {
	server = createServer((req, res) => {
		res.setHeader('content-type', 'text/html')
		if (req.url === '/tall') return void res.end('<!doctype html><title>tall</title><div style="height:5000px">tall</div>')
		if (req.url === '/form') return void res.end('<!doctype html><title>form</title><input name="draft">')
		res.end('<!doctype html><title>other</title>')
	})
	await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve))
	base = `http://127.0.0.1:${(server.address() as AddressInfo).port}`
})

test.afterAll(() => new Promise<void>((resolve) => server.close(() => resolve())))

/** Moves the app's test clock forward (TestHooks.TryHandle in the host). */
async function advanceClock(chrome: Page, ms: number): Promise<void> {
	await chrome.evaluate((by) => (globalThis as unknown as { chrome: { webview: { postMessage(m: unknown): void } } }).chrome.webview.postMessage({ kind: 'test', action: 'advance-clock', ms: by }), ms)
}

const tab = async (chrome: Page, id: string) => (await chrome.evaluate(() => window.zing.getTabs())).tabs.find((t) => t.id === id)!

async function openTab(chrome: Page, path: string): Promise<string> {
	await chrome.evaluate(() => window.zing.runCommand('tab.new'))
	const { activeId } = await chrome.evaluate(() => window.zing.getTabs())
	await chrome.evaluate(([id, url]) => window.zing.navigateTab(id!, url!), [activeId!, `${base}${path}`])
	return activeId!
}

test('an idle tab sleeps without a page and wakes at its URL and scroll position', async () => {
	const zing = await launchZing({ env: TEST_CLOCK })
	await zing.chrome.evaluate(() => window.zing.setSetting('sleepingTabs', { enabled: true, afterMinutes: 1, sleepPinned: false }))
	const first = await openTab(zing.chrome, '/tall')
	const tall = await pageWhere(zing.browser, (p) => p.url() === `${base}/tall`)
	await tall.evaluate(() => (globalThis as unknown as PageGlobal).scrollTo(0, 800))
	await expect.poll(() => tall.evaluate(() => (globalThis as unknown as PageGlobal).scrollY)).toBe(800)
	const second = await openTab(zing.chrome, '/other')

	await advanceClock(zing.chrome, 3 * MINUTE)
	await expect.poll(async () => (await tab(zing.chrome, first)).sleeping).toBe(true)
	expect((await tab(zing.chrome, first)).url).toBe(`${base}/tall`)
	expect((await tab(zing.chrome, second)).sleeping).toBe(false)
	await expect.poll(() => pages(zing.browser).some((p) => p.url() === `${base}/tall`)).toBe(false)

	await zing.chrome.evaluate((id) => window.zing.activateTab(id), first)
	expect((await tab(zing.chrome, first)).sleeping).toBe(false)
	const woken = await pageWhere(zing.browser, (p) => p.url() === `${base}/tall`)
	await expect.poll(() => woken.evaluate(() => (globalThis as unknown as PageGlobal).scrollY)).toBe(800)
	await zing.close()
})

test('a tab with unsaved form input stays awake', async () => {
	const zing = await launchZing({ env: TEST_CLOCK })
	await zing.chrome.evaluate(() => window.zing.setSetting('sleepingTabs', { enabled: true, afterMinutes: 1, sleepPinned: false }))
	const form = await openTab(zing.chrome, '/form')
	const page = await pageWhere(zing.browser, (p) => p.url() === `${base}/form`)
	await page.locator('input[name=draft]').fill('half a thought')
	await openTab(zing.chrome, '/other')
	await advanceClock(zing.chrome, 3 * MINUTE)
	// Give the sweep time to run, then check it left the tab alone.
	await new Promise((resolve) => setTimeout(resolve, 2000))
	expect((await tab(zing.chrome, form)).sleeping).toBe(false)
	await zing.close()
})
