import { createServer, type Server, type ServerResponse } from 'node:http'
import type { AddressInfo } from 'node:net'
import { expect, type Page } from '@playwright/test'
import type { ZingApi } from '../src/shared/ipc'
import { launchZing, test, pages, pageWhere } from './zing'

declare const window: { readonly zing: ZingApi }

type PageGlobal = { scrollTo(x: number, y: number): void; scrollY: number }

const MINUTE = 60_000
const TEST_CLOCK = { ZING_TEST_CLOCK: '1' }

let server: Server
let base = ''
const hanging: ServerResponse[] = []

test.beforeAll(async () => {
	server = createServer((req, res) => {
		res.setHeader('content-type', 'text/html')
		if (req.url === '/tall') return void res.end('<!doctype html><title>tall</title><div style="height:5000px">tall</div>')
		if (req.url === '/form') return void res.end('<!doctype html><title>form</title><input name="draft">')
		// As React does for a controlled input: the value attribute follows what was typed, so value equals defaultValue.
		if (req.url === '/react') return void res.end('<!doctype html><title>react</title><input name="draft"><script>const i = document.querySelector("input"); i.addEventListener("input", () => (i.defaultValue = i.value))</script>')
		if (req.url === '/framed') return void res.end('<!doctype html><title>framed</title><iframe src="/react"></iframe>')
		// An upload the server never answers, so it stays in progress.
		if (req.url === '/upload') return void hanging.push(res)
		res.end('<!doctype html><title>other</title>')
	})
	await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve))
	base = `http://127.0.0.1:${(server.address() as AddressInfo).port}`
})

test.afterAll(() => {
	for (const res of hanging) res.end()
	return new Promise<void>((resolve) => server.close(() => resolve()))
})

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

test('auto-close closes a tab left alone past its rule, and keeps the one on screen', async () => {
	const zing = await launchZing({ env: TEST_CLOCK })
	await zing.chrome.evaluate(() => window.zing.setSetting('autoCloseRule', 'hour'))
	await zing.chrome.evaluate(() => window.zing.setSetting('autoCloseEnabled', true))
	const left = await openTab(zing.chrome, '/other')
	const shown = await openTab(zing.chrome, '/tall')
	await advanceClock(zing.chrome, 30 * MINUTE)
	expect((await zing.chrome.evaluate(() => window.zing.getTabs())).tabs.map((t) => t.id)).toContain(left)
	await advanceClock(zing.chrome, 45 * MINUTE)
	await expect.poll(async () => (await zing.chrome.evaluate(() => window.zing.getTabs())).tabs.map((t) => t.id)).not.toContain(left)
	expect((await zing.chrome.evaluate(() => window.zing.getTabs())).activeId).toBe(shown)
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

/** Opens another tab, moves the clock past the sleep time and gives the sweep time to run. */
async function leaveIdle(chrome: Page): Promise<void> {
	await openTab(chrome, '/other')
	await advanceClock(chrome, 3 * MINUTE)
	await new Promise((resolve) => setTimeout(resolve, 2000))
}

test('a tab with a draft typed into a React-style input stays awake, in the page or a same-origin frame', async () => {
	const zing = await launchZing({ env: TEST_CLOCK })
	await zing.chrome.evaluate(() => window.zing.setSetting('sleepingTabs', { enabled: true, afterMinutes: 1, sleepPinned: false }))
	const top = await openTab(zing.chrome, '/react')
	await (await pageWhere(zing.browser, (p) => p.url() === `${base}/react`)).locator('input[name=draft]').fill('half a thought')
	const framed = await openTab(zing.chrome, '/framed')
	await (await pageWhere(zing.browser, (p) => p.url() === `${base}/framed`)).frameLocator('iframe').locator('input[name=draft]').fill('another')
	await leaveIdle(zing.chrome)
	expect((await tab(zing.chrome, top)).sleeping).toBe(false)
	expect((await tab(zing.chrome, framed)).sleeping).toBe(false)
	await zing.close()
})

test('a tab with an upload in progress stays awake', async () => {
	const zing = await launchZing({ env: TEST_CLOCK })
	await zing.chrome.evaluate(() => window.zing.setSetting('sleepingTabs', { enabled: true, afterMinutes: 1, sleepPinned: false }))
	const uploading = await openTab(zing.chrome, '/other')
	const page = await pageWhere(zing.browser, (p) => p.url() === `${base}/other`)
	await page.evaluate(() => {
		const g = globalThis as unknown as { XMLHttpRequest: new () => { open(m: string, u: string): void; send(b: unknown): void }; Blob: new (parts: string[]) => unknown }
		const xhr = new g.XMLHttpRequest()
		xhr.open('POST', '/upload')
		xhr.send(new g.Blob(['file contents']))
	})
	await leaveIdle(zing.chrome)
	expect((await tab(zing.chrome, uploading)).sleeping).toBe(false)
	await zing.close()
})

test('a tab using the camera stays awake', async () => {
	const zing = await launchZing({ env: TEST_CLOCK })
	await zing.chrome.evaluate(() => window.zing.setSetting('sleepingTabs', { enabled: true, afterMinutes: 1, sleepPinned: false }))
	const call = await openTab(zing.chrome, '/tall')
	const page = await pageWhere(zing.browser, (p) => p.url() === `${base}/tall`)
	const result = () => page.evaluate(() => (globalThis as unknown as { result?: string }).result)
	await page.evaluate(() => {
		const g = globalThis as unknown as { result?: string; navigator: { mediaDevices: { getUserMedia(c: object): Promise<unknown> } } }
		g.result = 'pending'
		g.navigator.mediaDevices.getUserMedia({ video: true }).then(
			() => (g.result = 'granted'),
			(error: Error) => (g.result = error.name),
		)
	})
	const ui = await pageWhere(zing.browser, (p) => p.url().endsWith('#pageui'))
	await ui.getByRole('button', { name: 'Allow' }).click()
	await expect.poll(result).toBe('granted')
	await leaveIdle(zing.chrome)
	expect((await tab(zing.chrome, call)).sleeping).toBe(false)
	await zing.close()
})
