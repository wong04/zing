import { createServer, type Server } from 'node:http'
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
/** Uploads the server has read and not yet answered, so they stay in flight until the test lets them finish. */
const uploads: (() => void)[] = []

test.beforeAll(async () => {
	server = createServer((req, res) => {
		if (req.url === '/sink') {
			req.resume()
			req.on('end', () => uploads.push(() => res.end('ok')))
			return
		}
		res.setHeader('content-type', 'text/html')
		// A step of a tab's history, titled after its path.
		if (req.url?.startsWith('/step/')) return void res.end(`<!doctype html><title>${req.url.slice('/step/'.length)}</title>`)
		if (req.url === '/tall') return void res.end('<!doctype html><title>tall</title><div style="height:5000px">tall</div>')
		if (req.url === '/form') return void res.end('<!doctype html><title>form</title><input name="draft">')
		// As React does for a controlled input: the value attribute follows what was typed, so value equals defaultValue.
		if (req.url === '/react') return void res.end('<!doctype html><title>react</title><input name="draft"><script>const i = document.querySelector("input"); i.addEventListener("input", () => (i.defaultValue = i.value))</script>')
		if (req.url === '/framed') return void res.end('<!doctype html><title>framed</title><iframe src="/react"></iframe>')
		// An editor that writes its own frame, as CKEditor 4 and TinyMCE do: document.open() replaces the frame's document.
		if (req.url === '/written') return void res.end('<!doctype html><title>written</title><iframe src="/other"></iframe><script>const f = document.querySelector("iframe"); f.onload = () => { const d = f.contentDocument; d.open(); d.write("<textarea name=draft></textarea>"); d.close(); const t = d.querySelector("textarea"); t.addEventListener("input", () => (t.defaultValue = t.value)); document.title = "written ready" }</script>')
		if (req.url === '/controls') return void res.end('<!doctype html><title>controls</title><input type="checkbox"><input type="range"><select><option>a</option><option>b</option></select>')
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
	const react = await pageWhere(zing.browser, (p) => p.url() === `${base}/react`)
	await react.locator('input[name=draft]').fill('half a thought')
	// A draft the page has hidden (a collapsed reply box, another step of a wizard) is still the person's.
	await react.evaluate(() => ((globalThis as unknown as { document: { querySelector(s: string): { style: { display: string } } } }).document.querySelector('input')!.style.display = 'none'))
	// The watch runs in an isolated world: the page sees no trace of it, and its natives are untouched.
	expect(await react.evaluate(() => Object.getOwnPropertyNames(globalThis).includes('zingSleep'))).toBe(false)
	expect(await react.evaluate(() => [fetch, XMLHttpRequest.prototype.send].every((f) => f.toString().includes('[native code]')))).toBe(true)
	const framed = await openTab(zing.chrome, '/framed')
	await (await pageWhere(zing.browser, (p) => p.url() === `${base}/framed`)).frameLocator('iframe').locator('input[name=draft]').fill('another')
	await leaveIdle(zing.chrome)
	expect((await tab(zing.chrome, top)).sleeping).toBe(false)
	expect((await tab(zing.chrome, framed)).sleeping).toBe(false)
	await zing.close()
})

test('checking a box, moving a slider or picking an option does not keep a tab awake', async () => {
	const zing = await launchZing({ env: TEST_CLOCK })
	await zing.chrome.evaluate(() => window.zing.setSetting('sleepingTabs', { enabled: true, afterMinutes: 1, sleepPinned: false }))
	const controls = await openTab(zing.chrome, '/controls')
	const page = await pageWhere(zing.browser, (p) => p.url() === `${base}/controls`)
	await page.locator('input[type=checkbox]').click()
	await page.locator('input[type=range]').focus()
	await page.keyboard.press('ArrowRight')
	await page.locator('select').selectOption('b')
	await leaveIdle(zing.chrome)
	await expect.poll(async () => (await tab(zing.chrome, controls)).sleeping).toBe(true)
	await zing.close()
})

test('a tab showing a live camera stays awake, and sleeps once the camera stops', async () => {
	const zing = await launchZing({ env: TEST_CLOCK })
	await zing.chrome.evaluate(() => window.zing.setSetting('sleepingTabs', { enabled: true, afterMinutes: 1, sleepPinned: false }))
	const call = await openTab(zing.chrome, '/tall')
	const page = await pageWhere(zing.browser, (p) => p.url() === `${base}/tall`)
	type Track = { stop(): void }
	type Camera = { result?: string; stream?: { getTracks(): Track[] }; document: { body: { append(el: unknown): void }; createElement(tag: string): { srcObject: unknown } }; navigator: { mediaDevices: { getUserMedia(c: object): Promise<{ getTracks(): Track[] }> } } }
	const result = () => page.evaluate(() => (globalThis as unknown as Camera).result)
	await page.evaluate(() => {
		const g = globalThis as unknown as Camera
		g.result = 'pending'
		g.navigator.mediaDevices.getUserMedia({ video: true }).then(
			(stream) => {
				// A self view, as a call shows.
				const video = g.document.createElement('video')
				video.srcObject = stream
				g.document.body.append(video)
				g.stream = stream
				g.result = 'granted'
			},
			(error: Error) => (g.result = error.name),
		)
	})
	const ui = await pageWhere(zing.browser, (p) => p.url().endsWith('#pageui'))
	await ui.getByRole('button', { name: 'Allow' }).click()
	await expect.poll(result).toBe('granted')
	await leaveIdle(zing.chrome)
	expect((await tab(zing.chrome, call)).sleeping).toBe(false)

	await page.evaluate(() => (globalThis as unknown as Camera).stream!.getTracks().forEach((track) => track.stop()))
	// A held tab is looked at again after TabSleeper.RetryMs.
	await advanceClock(zing.chrome, 6 * MINUTE)
	await expect.poll(async () => (await tab(zing.chrome, call)).sleeping).toBe(true)
	await zing.close()
})

test('a draft typed into a frame the page wrote with document.open() keeps the tab awake', async () => {
	const zing = await launchZing({ env: TEST_CLOCK })
	await zing.chrome.evaluate(() => window.zing.setSetting('sleepingTabs', { enabled: true, afterMinutes: 1, sleepPinned: false }))
	const editor = await openTab(zing.chrome, '/written')
	const page = await pageWhere(zing.browser, (p) => p.url() === `${base}/written`)
	await expect(page).toHaveTitle('written ready')
	await page.frameLocator('iframe').locator('textarea[name=draft]').fill('a long reply')
	await leaveIdle(zing.chrome)
	expect((await tab(zing.chrome, editor)).sleeping).toBe(false)
	await zing.close()
})

test('a tab stuck on a dialog is held, and the other idle tabs still sleep', async () => {
	const zing = await launchZing({ env: TEST_CLOCK })
	await zing.chrome.evaluate(() => window.zing.setSetting('sleepingTabs', { enabled: true, afterMinutes: 1, sleepPinned: false }))
	const stuck = await openTab(zing.chrome, '/form')
	const page = await pageWhere(zing.browser, (p) => p.url() === `${base}/form`)
	// Left open: a handled dialog that is never answered keeps the page's script blocked, so it can't answer the probe.
	page.on('dialog', () => {})
	await page.evaluate(() => void setTimeout(() => (globalThis as unknown as { alert(m: string): void }).alert('still here?'), 0))
	const idle = await openTab(zing.chrome, '/tall')
	await leaveIdle(zing.chrome)
	await expect.poll(async () => (await tab(zing.chrome, idle)).sleeping).toBe(true)
	expect((await tab(zing.chrome, stuck)).sleeping).toBe(false)
	await zing.close()
})

/** Sends the tab to a step of its history and waits until the tab shows it. */
async function step(chrome: Page, id: string, name: string): Promise<void> {
	await chrome.evaluate(([tab, url]) => window.zing.navigateTab(tab!, url!), [id, `${base}/step/${name}`])
	await expect.poll(async () => (await tab(chrome, id)).title).toBe(name)
}

async function command(chrome: Page, id: 'history.back' | 'history.forward', expected: string, tabId: string): Promise<void> {
	expect((await chrome.evaluate((c) => window.zing.runCommand(c), id)).status).toBe('done')
	await expect.poll(async () => (await tab(chrome, tabId)).url).toBe(`${base}/step/${expected}`)
}

test('a tab that slept goes back and forward through the history it had', async () => {
	const zing = await launchZing({ env: TEST_CLOCK })
	await zing.chrome.evaluate(() => window.zing.setSetting('sleepingTabs', { enabled: true, afterMinutes: 1, sleepPinned: false }))
	const walked = await openTab(zing.chrome, '/step/a')
	await expect.poll(async () => (await tab(zing.chrome, walked)).title).toBe('a')
	await step(zing.chrome, walked, 'b')
	await step(zing.chrome, walked, 'c')
	await command(zing.chrome, 'history.back', 'b', walked)
	await expect.poll(async () => (await tab(zing.chrome, walked)).canGoForward).toBe(true)
	// Gives the engine's history time to reach the host before the page goes.
	await new Promise((resolve) => setTimeout(resolve, 500))

	await openTab(zing.chrome, '/other')
	await advanceClock(zing.chrome, 3 * MINUTE)
	await expect.poll(async () => (await tab(zing.chrome, walked)).sleeping).toBe(true)
	await zing.chrome.evaluate((id) => window.zing.activateTab(id), walked)
	const woken = await pageWhere(zing.browser, (p) => p.url() === `${base}/step/b`)
	await expect(woken).toHaveTitle('b')
	expect(await tab(zing.chrome, walked)).toMatchObject({ canGoBack: true, canGoForward: true })

	await command(zing.chrome, 'history.back', 'a', walked)
	await expect(woken).toHaveTitle('a')
	await expect.poll(async () => (await tab(zing.chrome, walked)).canGoBack).toBe(false)
	await command(zing.chrome, 'history.forward', 'b', walked)
	await command(zing.chrome, 'history.forward', 'c', walked)
	await expect(woken).toHaveTitle('c')
	await expect.poll(async () => (await tab(zing.chrome, walked)).canGoForward).toBe(false)

	// A new page from there drops nothing behind it: Back walks the page's own history, then the one it had.
	await step(zing.chrome, walked, 'd')
	await command(zing.chrome, 'history.back', 'c', walked)
	await command(zing.chrome, 'history.back', 'b', walked)
	await zing.close()
})

test('a tab sending an upload stays awake, and sleeps once the upload is done', async () => {
	const zing = await launchZing({ env: TEST_CLOCK })
	await zing.chrome.evaluate(() => window.zing.setSetting('sleepingTabs', { enabled: true, afterMinutes: 1, sleepPinned: false }))
	const sending = await openTab(zing.chrome, '/form')
	const page = await pageWhere(zing.browser, (p) => p.url() === `${base}/form`)
	type Uploader = { fetch(url: string, init: object): Promise<unknown>; Blob: new (parts: unknown[]) => unknown; Uint8Array: new (n: number) => unknown }
	// A photo's worth of bytes, as a file picker would hand the page.
	await page.evaluate(() => {
		const g = globalThis as unknown as Uploader
		void g.fetch('/sink', { method: 'POST', body: new g.Blob([new g.Uint8Array(2_000_000)]) })
	})
	await expect.poll(() => uploads.length).toBe(1)
	await leaveIdle(zing.chrome)
	expect((await tab(zing.chrome, sending)).sleeping).toBe(false)

	uploads.splice(0).forEach((finish) => finish())
	// A held tab is looked at again after TabSleeper.RetryMs.
	await advanceClock(zing.chrome, 6 * MINUTE)
	await expect.poll(async () => (await tab(zing.chrome, sending)).sleeping).toBe(true)
	await zing.close()
})
