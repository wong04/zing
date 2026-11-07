import { existsSync, readdirSync } from 'node:fs'
import { createServer, type Server } from 'node:http'
import type { AddressInfo } from 'node:net'
import { join } from 'node:path'
import { expect, type Page } from '@playwright/test'
import type { ZingApi } from '../src/shared/ipc'
import { launchZing, pageWhere, test } from './zing'

declare const window: { readonly zing: ZingApi }

// The security paths the review (M15) found untested: what Zing's own views may load, what another app may hand over,
// what a server may name a download, and whose permission prompt is shown.

let server: Server
let base = ''
let other = ''
/** Every path the server was asked for, in order: a guard that held leaves its path out. */
const hits: string[] = []

/** Each download name a server could send, percent-encoded as `filename*` carries it. */
const NAMES: Record<string, string> = {
	bidi: 'invoice\u202Efdp.exe',
	stream: 'report.pdf:x',
	backslash: 'x.\\sub\\f',
	reserved: 'CON.txt',
}

test.beforeAll(async () => {
	server = createServer((req, res) => {
		const path = req.url ?? '/'
		hits.push(path)
		const name = /^\/name\/(\w+)$/.exec(path)?.[1]
		if (name !== undefined) {
			res.setHeader('content-type', 'application/octet-stream')
			res.setHeader('content-disposition', `attachment; filename*=UTF-8''${encodeURIComponent(NAMES[name] ?? 'unknown')}`)
			return void res.end(Buffer.alloc(64, 1))
		}
		res.setHeader('content-type', 'text/html')
		if (path === '/framed') return void res.end(`<!doctype html><title>Framed</title><iframe src="${other}/inner" allow="camera"></iframe>`)
		res.end(`<!doctype html><title>Page ${path}</title>`)
	})
	await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve))
	const port = (server.address() as AddressInfo).port
	base = `http://127.0.0.1:${port}`
	// The same server under another name: another origin, for a cross-origin frame.
	other = `http://localhost:${port}`
})

test.afterAll(() => new Promise<void>((resolve) => server.close(() => resolve())))

const tabsOf = (chrome: Page) => chrome.evaluate(() => window.zing.getTabs())

/** A request that must arrive (a page tab may load it): once it has, any refused one before it would have too. */
async function control(chrome: Page, name: string): Promise<void> {
	await chrome.evaluate(async (url) => {
		const { activeId } = await window.zing.getTabs()
		await window.zing.navigateTab(activeId!, url)
	}, `${base}/control/${name}`)
	await expect.poll(() => hits).toContain(`/control/${name}`)
}

test("Zing's own view loads nothing from the web: not by location, window.open, a form or an iframe", async () => {
	const zing = await launchZing()
	const home = zing.chrome.url()
	await zing.chrome.evaluate((url) => {
		const doc = (globalThis as unknown as { document: Document }).document
		const frame = doc.createElement('iframe')
		frame.src = `${url}/frame`
		doc.body.append(frame)
		;(globalThis as unknown as { open(u: string): unknown }).open(`${url}/opened`)
		const form = doc.createElement('form')
		form.method = 'post'
		form.action = `${url}/form`
		doc.body.append(form)
		form.submit()
	}, base)
	// A navigation of the view itself cancels; the evaluate may not answer if it went.
	await zing.chrome.evaluate((url) => void ((globalThis as unknown as { location: { href: string } }).location.href = `${url}/location`), base).catch(() => undefined)
	await control(zing.chrome, 'guards')
	expect(hits.filter((path) => ['/frame', '/opened', '/form', '/location'].includes(path))).toEqual([])
	expect(zing.chrome.url()).toBe(home)
	expect((await tabsOf(zing.chrome)).tabs).toHaveLength(1)
	await zing.close()
})

test('another launch hands over only web links, never a file, script, zing: address or a profile switch', async () => {
	const zing = await launchZing()
	for (const arg of ['file:///C:/Windows/win.ini', 'javascript:alert(1)', 'zing://visit-anyway?to=https://a.test', '--user-data-dir=C:\\elsewhere']) {
		expect(await zing.launchAgain([arg])).toBe(0)
	}
	const good = `${base}/handed-over`
	expect(await zing.launchAgain([good])).toBe(0)
	// The link takes the blank tab Zing starts with; nothing handed over before it opened anything.
	await expect.poll(async () => (await tabsOf(zing.chrome)).tabs.map((t) => t.url)).toEqual([good])
	await zing.close()
})

test('a server cannot name a download to hide its type, write a stream, reach a folder or name a device', async () => {
	const zing = await launchZing()
	const directory = join(zing.userDataDir, 'downloads')
	const files = () => (existsSync(directory) ? readdirSync(directory, { recursive: true }).length : 0)
	for (const name of Object.keys(NAMES)) {
		const count = files()
		await zing.chrome.evaluate(async (url) => {
			const { activeId } = await window.zing.getTabs()
			await window.zing.navigateTab(activeId!, url)
		}, `${base}/name/${name}`)
		await expect.poll(files).toBe(count + 1)
	}
	// Stream names (report.pdf:x) don't list, so every file is a plain one at the top of the folder.
	const saved = readdirSync(directory, { recursive: true, withFileTypes: true })
	expect(saved.every((entry) => entry.isFile() && entry.parentPath === directory)).toBe(true)
	expect(saved.map((entry) => entry.name).sort()).toEqual(['CON_.txt', 'invoicefdp.exe', 'report.pdfx', 'x.subf'])
	await zing.close()
})

// Chromium's permission delegation: a frame let in with allow="camera" asks as the page that embedded it, which is
// the site the person sees in the address bar and the one the grant is kept for.
test('a cross-origin frame asking for the camera is asked as the page that let it in', async () => {
	const zing = await launchZing({ env: { ZING_START_URL: `${base}/framed` } })
	const page = await pageWhere(zing.browser, (p) => p.url() === `${base}/framed`)
	await page.waitForLoadState('load')
	await expect.poll(() => page.frames().some((frame) => frame.url() === `${other}/inner`)).toBe(true)
	const inner = page.frames().find((frame) => frame.url() === `${other}/inner`)!
	void inner.evaluate(() => (globalThis as unknown as { navigator: { mediaDevices: { getUserMedia(c: object): Promise<unknown> } } }).navigator.mediaDevices.getUserMedia({ video: true }).catch(() => undefined))
	const ui = await pageWhere(zing.browser, (p) => p.url().endsWith('#pageui'))
	await expect(ui.getByRole('alertdialog')).toContainText(`${base} wants to use your camera`)
	await ui.getByRole('button', { name: 'Deny' }).click()
	await expect(ui.getByRole('alertdialog')).toHaveCount(0)
	await zing.close()
})

test('a prompt goes when its tab navigates away, and when its tab is closed', async () => {
	const zing = await launchZing({ env: { ZING_START_URL: `${base}/asks` } })
	const asking = async () => {
		const page = await pageWhere(zing.browser, (p) => p.url() === `${base}/asks`)
		await page.waitForLoadState('load')
		return page
	}
	// Notifications first, then the camera: the engine keeps a refused notification request refused for the session.
	await (await asking()).evaluate(() => void (globalThis as unknown as { Notification: { requestPermission(): Promise<string> } }).Notification.requestPermission())
	const ui = await pageWhere(zing.browser, (p) => p.url().endsWith('#pageui'))
	await expect(ui.getByRole('alertdialog')).toBeVisible()
	const { activeId } = await tabsOf(zing.chrome)
	await zing.chrome.evaluate(([id, url]) => window.zing.navigateTab(id!, url!), [activeId, `${base}/elsewhere`])
	await expect(ui.getByRole('alertdialog')).toHaveCount(0)

	await zing.chrome.evaluate(([id, url]) => window.zing.navigateTab(id!, url!), [activeId, `${base}/asks`])
	void (await asking()).evaluate(() => (globalThis as unknown as { navigator: { mediaDevices: { getUserMedia(c: object): Promise<unknown> } } }).navigator.mediaDevices.getUserMedia({ video: true }).catch(() => undefined)).catch(() => undefined)
	await expect(ui.getByRole('alertdialog')).toBeVisible()
	await zing.chrome.evaluate(() => window.zing.runCommand('tab.new'))
	await zing.chrome.evaluate((id) => window.zing.closeTab(id!), activeId)
	await expect(ui.getByRole('alertdialog')).toHaveCount(0)
	await zing.close()
})
