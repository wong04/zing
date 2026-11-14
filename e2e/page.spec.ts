import { existsSync, readdirSync, readFileSync, statSync } from 'node:fs'
import { join } from 'node:path'
import { createServer, type Server } from 'node:http'
import type { AddressInfo } from 'node:net'
import { expect } from '@playwright/test'
import type { ZingApi } from '../src/shared/ipc'
import { launchZing, test, layers, pages, pageWhere, setCursor, exitFullScreenButton } from './zing'

declare const window: { readonly zing: ZingApi }

const PARAGRAPH = 'Zing keeps nothing by default, so the reader has to build its view from the page that is open right now. '.repeat(4)

// A 1×1 red PNG.
const PNG = 'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFBQIAX8jx0gAAAABJRU5ErkJggg=='

let server: Server
let base = ''
let framedZipServed = false
let framedFileServed = false

test.beforeAll(async () => {
	server = createServer((req, res) => {
		if (req.url === '/file.bin') {
			res.setHeader('content-type', 'application/octet-stream')
			res.setHeader('content-disposition', 'attachment; filename="report.bin"')
			return void res.end(Buffer.alloc(2048, 7))
		}
		// A type the engine can't show, with no Content-Disposition (DownloadPolicy, L12).
		if (req.url === '/archive.zip' || req.url === '/inner.zip') {
			if (req.url === '/inner.zip') framedZipServed = true
			res.setHeader('content-type', 'application/zip')
			return void res.end(Buffer.alloc(1024, 3))
		}
		// The server marking a frame's response as a file: Krackn saves it, frame or not.
		if (req.url === '/inner-file.bin') {
			framedFileServed = true
			res.setHeader('content-type', 'application/zip')
			res.setHeader('content-disposition', 'attachment; filename="framed.zip"')
			return void res.end(Buffer.alloc(512, 5))
		}
		res.setHeader('content-type', 'text/html')
		if (req.url === '/linked') return void res.end('<!doctype html><title>Linked</title><a href="/archive.zip">Get the archive</a>')
		if (req.url === '/framed-file') return void res.end('<!doctype html><title>Framed file</title><iframe src="/inner-file.bin"></iframe>')
		if (req.url === '/framed') return void res.end('<!doctype html><title>Framed</title><iframe src="/inner.zip"></iframe>')
		if (req.url === '/perm') return void res.end('<!doctype html><title>Permissions</title>')
		if (req.url === '/article') return void res.end(`<!doctype html><title>Fixture Headline</title><nav><a href="/other">Site menu</a></nav><article><h1>Fixture Headline</h1><p>${PARAGRAPH}</p><p>${PARAGRAPH}</p></article><footer>Footer junk</footer>`)
		if (req.url === '/short') return void res.end('<!doctype html><title>Short</title><p>Too little to read.</p>')
		if (req.url === '/icon.png') {
			res.setHeader('content-type', 'image/png')
			return void res.end(Buffer.from(PNG, 'base64'))
		}
		if (req.url === '/iconic') return void res.end('<!doctype html><title>Iconic</title><link rel="icon" href="/icon.png">')
		res.end('<!doctype html><title>Find</title><p>needle one</p><p>needle two</p><p>needle three</p><p>hay</p>')
	})
	await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve))
	base = `http://127.0.0.1:${(server.address() as AddressInfo).port}`
})

test.afterAll(() => new Promise<void>((resolve) => server.close(() => resolve())))

test('find in page shows a match count and steps through matches', async () => {
	const zing = await launchZing({ env: { ZING_START_URL: `${base}/find` } })
	await pageWhere(zing.browser, (p) => p.url() === `${base}/find`)
	await zing.chrome.evaluate(() => window.zing.runCommand('page.find'))
	const ui = await pageWhere(zing.browser, (p) => p.url().endsWith('#pageui'))
	await ui.getByRole('textbox', { name: 'Find in page' }).fill('needle')
	await expect(ui.getByText('1 of 3')).toBeVisible()
	// The DOM had the bar all along; what was missing is a view tall enough to show it.
	await expect.poll(async () => (await layers(zing.chrome)).find((l) => l.url.endsWith('#pageui'))).toMatchObject({ visible: true })
	expect((await layers(zing.chrome)).find((l) => l.url.endsWith('#pageui'))!.height).toBeGreaterThan(20)
	await zing.chrome.evaluate(() => window.zing.runCommand('page.findNext'))
	await expect(ui.getByText('2 of 3')).toBeVisible()
	await zing.chrome.evaluate(() => window.zing.runCommand('page.findPrevious'))
	await expect(ui.getByText('1 of 3')).toBeVisible()
	await zing.chrome.evaluate(() => window.zing.closeFind())
	await expect.poll(async () => (await zing.chrome.evaluate(() => window.zing.getPageUi())).find ?? null).toBeNull()
	await zing.close()
})

test('a download lands in the downloads folder, shows in the list and numbers on collision', async () => {
	const zing = await launchZing()
	const directory = join(zing.userDataDir, 'downloads')
	const download = () =>
		zing.chrome.evaluate(async (url) => {
			const { activeId } = await window.zing.getTabs()
			await window.zing.navigateTab(activeId!, url)
		}, `${base}/file.bin`)
	await download()
	await expect.poll(() => existsSync(join(directory, 'report.bin')) && statSync(join(directory, 'report.bin')).size).toBe(2048)
	// Edge's download manager writes the Mark of the Web itself, with the real source.
	const mark = join(directory, 'report.bin:Zone.Identifier')
	// The engine stamps its generic mark after the download; Zing puts its own back within 100 ms.
	await expect.poll(() => existsSync(mark) && readFileSync(mark, 'utf8')).toContain(`HostUrl=${base}/file.bin`)
	expect(readFileSync(mark, 'utf8')).toContain('ZoneId=3')
	const ui = await pageWhere(zing.browser, (p) => p.url().endsWith('#pageui'))
	await expect(ui.getByRole('button', { name: 'Show in folder' })).toBeVisible()
	await download()
	await expect.poll(() => readdirSync(directory).sort()).toEqual(['report 2.bin', 'report.bin'])
	// A navigation that became a download isn't a failed load: no "Couldn't load … (ConnectionAborted)".
	const notice = pages(zing.browser).find((p) => p.url().endsWith('#overlay'))
	if (notice) expect(await notice.locator('body').innerText()).not.toContain("Couldn't load")
	await zing.close()
})

// L12: Krackn's DownloadPolicy saves a main-frame response it can't show, but not the same response in a frame, where an
// ad's odd content type shouldn't drop a file in Downloads.
test('a main-frame response the engine cannot show, with no Content-Disposition, downloads', async () => {
	const zing = await launchZing()
	const directory = join(zing.userDataDir, 'downloads')
	await zing.chrome.evaluate(async (url) => {
		const { activeId } = await window.zing.getTabs()
		await window.zing.navigateTab(activeId!, url)
	}, `${base}/archive.zip`)
	await expect.poll(() => existsSync(join(directory, 'archive.zip')) && statSync(join(directory, 'archive.zip')).size).toBe(1024)
	await zing.close()
})

test('a top-level link to a response the engine cannot show downloads', async () => {
	const zing = await launchZing({ env: { ZING_START_URL: `${base}/linked` } })
	const directory = join(zing.userDataDir, 'downloads')
	const page = await pageWhere(zing.browser, (p) => p.url() === `${base}/linked`)
	await page.getByRole('link', { name: 'Get the archive' }).click()
	await expect.poll(() => existsSync(join(directory, 'archive.zip')) && statSync(join(directory, 'archive.zip')).size).toBe(1024)
	await zing.close()
})

test('a frame response the server marks as a file downloads', async () => {
	framedFileServed = false
	const zing = await launchZing({ env: { ZING_START_URL: `${base}/framed-file` } })
	const directory = join(zing.userDataDir, 'downloads')
	await pageWhere(zing.browser, (p) => p.url() === `${base}/framed-file`)
	await expect.poll(() => framedFileServed).toBe(true)
	await expect.poll(() => existsSync(join(directory, 'framed.zip')) && statSync(join(directory, 'framed.zip')).size).toBe(512)
	await zing.close()
})

test('the same response in a frame does not download', async () => {
	framedZipServed = false
	const zing = await launchZing({ env: { ZING_START_URL: `${base}/framed` } })
	const directory = join(zing.userDataDir, 'downloads')
	await pageWhere(zing.browser, (p) => p.url() === `${base}/framed`)
	await expect.poll(() => framedZipServed).toBe(true)
	// A main-frame download after the frame's response: once it has landed, a download the frame started would have too.
	await zing.chrome.evaluate(async (url) => {
		const { activeId } = await window.zing.getTabs()
		await window.zing.navigateTab(activeId!, url)
	}, `${base}/archive.zip`)
	await expect.poll(() => existsSync(join(directory, 'archive.zip')) && statSync(join(directory, 'archive.zip')).size).toBe(1024)
	await expect.poll(() => readdirSync(directory)).toEqual(['archive.zip'])
	await zing.close()
})

test('a permission request is asked in the page UI and denied when Deny is clicked', async () => {
	const zing = await launchZing({ env: { ZING_START_URL: `${base}/perm` } })
	const page = await pageWhere(zing.browser, (p) => p.url() === `${base}/perm`)
	await page.waitForLoadState('domcontentloaded')
	const result = () => page.evaluate(() => (globalThis as unknown as { result?: string }).result)
	await page.evaluate(() => {
		const g = globalThis as unknown as { result?: string; Notification: { requestPermission(): Promise<string> } }
		g.result = 'pending'
		void g.Notification.requestPermission().then((r) => (g.result = r))
	})
	const ui = await pageWhere(zing.browser, (p) => p.url().endsWith('#pageui'))
	await expect(ui.getByText('wants to show notifications')).toBeVisible()
	await expect.poll(async () => (await layers(zing.chrome)).find((l) => l.url.endsWith('#pageui'))?.height ?? 0).toBeGreaterThan(20)
	expect(await result()).toBe('pending')
	await ui.getByRole('button', { name: 'Deny' }).click()
	await expect.poll(result).toBe('denied')
	await zing.close()
})

test('Allow grants a notification, and the camera and microphone, each asked in turn', async () => {
	const zing = await launchZing({ env: { ZING_START_URL: `${base}/perm` } })
	const page = await pageWhere(zing.browser, (p) => p.url() === `${base}/perm`)
	await page.waitForLoadState('domcontentloaded')
	const result = () => page.evaluate(() => (globalThis as unknown as { result?: string }).result)

	await page.evaluate(() => {
		const g = globalThis as unknown as { result?: string; Notification: { requestPermission(): Promise<string> } }
		g.result = 'pending'
		void g.Notification.requestPermission().then((r) => (g.result = r))
	})
	// The page UI's view starts with the first prompt.
	const ui = await pageWhere(zing.browser, (p) => p.url().endsWith('#pageui'))
	await expect(ui.getByText('wants to show notifications')).toBeVisible()
	await ui.getByRole('button', { name: 'Allow' }).click()
	await expect.poll(result).toBe('granted')

	await page.evaluate(() => {
		const g = globalThis as unknown as {
			result?: string
			navigator: { mediaDevices: { getUserMedia(c: object): Promise<{ getTracks(): { kind: string }[] }> } }
		}
		g.result = 'pending'
		g.navigator.mediaDevices.getUserMedia({ video: true, audio: true }).then(
			(stream) => (g.result = stream.getTracks().map((t) => t.kind).sort().join(' ')),
			(error: Error) => (g.result = error.name),
		)
	})
	// The engine asks for each device on its own; both prompts name the site.
	await expect(ui.getByText(/wants to use your (camera|microphone)/)).toBeVisible()
	await ui.getByRole('button', { name: 'Allow' }).click()
	await expect.poll(async () => (await result()) !== 'pending' || (await ui.getByText(/wants to use your (camera|microphone)/).isVisible())).toBe(true)
	if ((await result()) === 'pending') await ui.getByRole('button', { name: 'Allow' }).click()
	await expect.poll(result).toBe('audio video')
	await zing.close()
})

test('a click that lands on Allow the moment a prompt opens is ignored', async () => {
	const zing = await launchZing({ env: { ZING_START_URL: `${base}/perm` } })
	const page = await pageWhere(zing.browser, (p) => p.url() === `${base}/perm`)
	await page.waitForLoadState('domcontentloaded')
	const result = () => page.evaluate(() => (globalThis as unknown as { result?: string }).result)
	const ask = () =>
		page.evaluate(() => {
			const g = globalThis as unknown as { result?: string; Notification: { requestPermission(): Promise<string> } }
			g.result = 'pending'
			void g.Notification.requestPermission().then((r) => (g.result = r))
		})
	// A first prompt brings up the page UI's view, so the second can be watched from the moment it is added.
	await ask()
	const ui = await pageWhere(zing.browser, (p) => p.url().endsWith('#pageui'))
	await expect(ui.getByText('wants to show notifications')).toBeVisible()
	await ui.getByRole('button', { name: 'Deny' }).click()
	await expect.poll(result).toBe('denied')
	await expect(ui.getByRole('alertdialog')).toHaveCount(0)

	// The second half of a double-click: a click on Allow as soon as the card is in the page.
	await ui.evaluate(() => {
		const g = globalThis as unknown as { earlyClicks?: number; document: Document; MutationObserver: typeof MutationObserver }
		g.earlyClicks = 0
		new g.MutationObserver(() => {
			const allow = g.document.querySelector<HTMLButtonElement>('[role="alertdialog"] button.primary')
			if (!allow || g.earlyClicks !== 0) return
			g.earlyClicks = 1
			allow.click()
		}).observe(g.document.body, { childList: true, subtree: true })
	})
	await page.evaluate(() => {
		const g = globalThis as unknown as {
			result?: string
			navigator: { mediaDevices: { getUserMedia(c: object): Promise<unknown> } }
		}
		g.result = 'pending'
		g.navigator.mediaDevices.getUserMedia({ video: true }).then(
			() => (g.result = 'granted'),
			(error: Error) => (g.result = error.name),
		)
	})
	await expect(ui.getByText('wants to use your camera')).toBeVisible()
	expect(await ui.evaluate(() => (globalThis as unknown as { earlyClicks: number }).earlyClicks)).toBe(1)
	await expect(ui.getByRole('button', { name: 'Allow' })).toBeEnabled()
	await ui.getByRole('button', { name: 'Deny' }).click()
	await expect.poll(result).toBe('NotAllowedError')
	await zing.close()
})

test("a background tab's prompt names that tab, and stops once the tab is shown", async () => {
	const zing = await launchZing({ env: { ZING_START_URL: `${base}/perm` } })
	const page = await pageWhere(zing.browser, (p) => p.url() === `${base}/perm`)
	await page.waitForLoadState('domcontentloaded')
	await zing.chrome.evaluate(() => window.zing.runCommand('tab.new'))
	await expect.poll(async () => (await zing.chrome.evaluate(() => window.zing.getTabs())).tabs.length).toBe(2)
	await page.evaluate(() => void (globalThis as unknown as { Notification: { requestPermission(): Promise<string> } }).Notification.requestPermission())
	const ui = await pageWhere(zing.browser, (p) => p.url().endsWith('#pageui'))
	await expect(ui.getByText('wants to show notifications')).toBeVisible()
	await expect(ui.getByText('From the tab “Permissions”')).toBeVisible()
	const first = (await zing.chrome.evaluate(() => window.zing.getTabs())).tabs[0]!.id
	await zing.chrome.evaluate((id) => window.zing.activateTab(id), first)
	await expect(ui.getByText('From the tab')).toHaveCount(0)
	await ui.getByRole('button', { name: 'Deny' }).click()
	await zing.close()
})

test("a page's icon reaches its tab as an in-memory image", async () => {
	const zing = await launchZing({ env: { ZING_START_URL: `${base}/iconic` } })
	await expect
		.poll(async () => (await zing.chrome.evaluate(() => window.zing.getTabs())).tabs[0]?.icon?.slice(0, 22))
		.toBe('data:image/png;base64,')
	await zing.close()
})

test('bigger text scales type in this tab and survives its next page', async () => {
	const zing = await launchZing({ env: { ZING_START_URL: `${base}/find` } })
	const page = await pageWhere(zing.browser, (p) => p.url() === `${base}/find`)
	const fontSize = () => page.evaluate(() => (globalThis as unknown as { getComputedStyle(e: unknown): { fontSize: string }; document: { documentElement: unknown } }).getComputedStyle((globalThis as unknown as { document: { documentElement: unknown } }).document.documentElement).fontSize)
	await expect.poll(fontSize).toBe('16px')
	await zing.chrome.evaluate(() => window.zing.runCommand('page.textBigger'))
	await expect.poll(fontSize).toBe('17.6px')
	await page.reload()
	await expect.poll(fontSize).toBe('17.6px')
	await zing.close()
})

test('Print opens the print preview for the page on screen', async () => {
	const zing = await launchZing({ env: { ZING_START_URL: `${base}/find` } })
	await pageWhere(zing.browser, (p) => p.url() === `${base}/find`)
	expect(await zing.chrome.evaluate(() => window.zing.runCommand('page.print'))).toMatchObject({ status: 'done' })
	await expect.poll(() => zing.dialogs()).toEqual([{ kind: 'print', url: `${base}/find` }])
	await zing.close()
})

test('a fullscreen element gives the page the whole window, and leaving puts the chrome back', async () => {
	const zing = await launchZing({ env: { ZING_START_URL: `${base}/find` } })
	const page = await pageWhere(zing.browser, (p) => p.url() === `${base}/find`)
	await page.waitForLoadState('load')
	const shown = async () => (await layers(zing.chrome)).find((l) => l.url === `${base}/find` && l.visible)
	const inChrome = await shown()
	expect(inChrome!.y).toBeGreaterThan(0)
	// Playwright's evaluate counts as a user gesture, which requestFullscreen needs.
	await page.evaluate(() => (globalThis as unknown as { document: { documentElement: { requestFullscreen(): Promise<void> } } }).document.documentElement.requestFullscreen())
	await expect.poll(async () => (await shown())?.y).toBe(0)
	expect((await shown())!.x).toBe(0)
	expect((await shown())!.height).toBeGreaterThan(inChrome!.height)
	// The page now covers the chrome, so Zing says at the top whose page has the screen and how to leave.
	const overlay = await pageWhere(zing.browser, (p) => p.url().endsWith('#overlay'))
	await expect(overlay.getByRole('status')).toHaveText(`${new URL(base).host} is now full screen · Press Esc to exit`)
	const hint = (await layers(zing.chrome)).find((l) => l.url.endsWith('#overlay') && l.visible)
	expect(hint!.y).toBeLessThan((await shown())!.height / 4)
	await page.evaluate(() => (globalThis as unknown as { document: { exitFullscreen(): Promise<void> } }).document.exitFullscreen())
	await expect.poll(async () => (await shown())?.y).toBe(inChrome!.y)
	expect((await shown())!.height).toBe(inChrome!.height)
	await expect(overlay.getByRole('status')).toHaveCount(0)
	await zing.close()
})

test('a fullscreen page shows an Exit full screen button while the cursor is at the top, and the button gives the chrome back', async () => {
	const zing = await launchZing({ env: { ZING_START_URL: `${base}/find` } })
	const page = await pageWhere(zing.browser, (p) => p.url() === `${base}/find`)
	await page.waitForLoadState('load')
	const shown = async () => (await layers(zing.chrome)).find((l) => l.url === `${base}/find` && l.visible)
	const exit = async () => (await layers(zing.chrome)).find((l) => l.url.endsWith('#fullscreen-exit') && l.visible)
	const fullscreen = () => page.evaluate(() => (globalThis as unknown as { document: { fullscreenElement: unknown } }).document.fullscreenElement !== null)
	const inChrome = await shown()
	// Playwright's evaluate counts as a user gesture, which requestFullscreen needs.
	await page.evaluate(() => (globalThis as unknown as { document: { documentElement: { requestFullscreen(): Promise<void> } } }).document.documentElement.requestFullscreen())
	await expect.poll(async () => (await shown())?.y).toBe(0)
	const { width, height } = (await shown())!
	// Over the middle of the page nothing reveals itself; reaching the top edge brings the button, centred near the top.
	await setCursor(zing.chrome, { x: width / 2, y: height / 2 })
	expect(await exit()).toBeUndefined()
	await setCursor(zing.chrome, { x: width / 2, y: 0 })
	await expect.poll(exit).toBeTruthy()
	const button = (await exit())!
	expect(Math.abs(button.x + button.width / 2 - width / 2)).toBeLessThanOrEqual(1)
	expect(button.y).toBeLessThan(height / 8)
	// Moving down past it puts it away; coming back brings it again.
	await setCursor(zing.chrome, { x: width / 2, y: height / 2 })
	await expect.poll(exit).toBeUndefined()
	expect(await fullscreen()).toBe(true)
	await setCursor(zing.chrome, { x: width / 2, y: 0 })
	await expect.poll(exit).toBeTruthy()
	await (await exitFullScreenButton(zing.browser)).click()
	await expect.poll(fullscreen).toBe(false)
	await expect.poll(async () => (await shown())?.y).toBe(inChrome!.y)
	expect((await shown())!.height).toBe(inChrome!.height)
	// It goes with fullscreen, wherever the cursor is.
	await expect.poll(exit).toBeUndefined()
	await setCursor(zing.chrome, null)
	await zing.close()
})

test('closing a window while its page is fullscreen closes cleanly', async () => {
	const zing = await launchZing({ env: { ZING_START_URL: `${base}/find` } })
	const page = await pageWhere(zing.browser, (p) => p.url() === `${base}/find`)
	await page.waitForLoadState('load')
	await page.evaluate(() => (globalThis as unknown as { document: { documentElement: { requestFullscreen(): Promise<void> } } }).document.documentElement.requestFullscreen())
	await expect.poll(async () => (await layers(zing.chrome)).find((l) => l.url === `${base}/find` && l.visible)?.y).toBe(0)
	await zing.close()
})

test('zoom steps the whole page in this tab, survives its next page, and actual size resets it', async () => {
	const zing = await launchZing({ env: { ZING_START_URL: `${base}/find` } })
	const page = await pageWhere(zing.browser, (p) => p.url() === `${base}/find`)
	// Page zoom scales CSS pixels, so the ratio of device to CSS pixels grows with it.
	const ratio = () => page.evaluate(() => (globalThis as unknown as { devicePixelRatio: number }).devicePixelRatio)
	await page.waitForLoadState('load')
	const actual = await ratio()
	await zing.chrome.evaluate(() => window.zing.runCommand('page.zoomIn'))
	await expect.poll(async () => (await ratio()) / actual).toBeCloseTo(1.1, 2)
	await page.goto(`${base}/short`)
	await expect.poll(async () => (await ratio()) / actual).toBeCloseTo(1.1, 2)
	await zing.chrome.evaluate(() => window.zing.runCommand('page.actualSize'))
	await expect.poll(ratio).toBeCloseTo(actual, 2)
	await zing.close()
})

test('reader mode swaps the page for its article in a themed view, and says so when there is none', async () => {
	const zing = await launchZing({ env: { ZING_START_URL: `${base}/article` } })
	const page = await pageWhere(zing.browser, (p) => p.url() === `${base}/article`)
	await page.waitForLoadState('load')
	await zing.chrome.evaluate(() => window.zing.runCommand('page.reader'))
	await expect(page.locator('main[data-zing-reader] h1')).toHaveText('Fixture Headline')
	await expect(page.locator('body')).toContainText('reader has to build its view')
	await expect(page.locator('body')).not.toContainText('Footer junk')
	// Leaving reader mode brings the page back.
	await zing.chrome.evaluate(() => window.zing.runCommand('page.reader'))
	await expect(page.locator('footer')).toHaveText('Footer junk')

	await zing.chrome.evaluate(async (url) => {
		const { activeId } = await window.zing.getTabs()
		await window.zing.navigateTab(activeId!, url)
	}, `${base}/short`)
	await pageWhere(zing.browser, (p) => p.url() === `${base}/short`)
	await zing.chrome.evaluate(() => window.zing.runCommand('page.reader'))
	const overlay = await pageWhere(zing.browser, (p) => p.url().endsWith('#overlay'))
	await expect(overlay.getByText("Reader can't find an article on this page")).toBeVisible()
	await zing.close()
})
