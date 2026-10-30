import { existsSync, mkdtempSync, readFileSync, readdirSync } from 'node:fs'
import { createServer, type Server } from 'node:http'
import type { AddressInfo } from 'node:net'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { expect, test, type ElectronApplication, type Page } from '@playwright/test'
import type { ZingApi } from '../src/shared/ipc'
import { launchZing } from './zing'

// The node tsconfig has no DOM lib; these callbacks run in the chrome renderer, where the preload defines it.
declare const window: { zing: ZingApi }

const PARAGRAPH =
	'Zing keeps nothing by default, so the reader has to build its view from the page that is open right now. '.repeat(4)

let server: Server
let base: string

test.beforeAll(async () => {
	server = createServer((req, res) => {
		const url = new URL(req.url ?? '/', 'http://localhost')
		const html = (body: string, head = '', title = url.pathname): void => {
			res.setHeader('content-type', 'text/html')
			res.end(`<!doctype html><html><head><title>${title}</title>${head}</head><body>${body}</body></html>`)
		}
		switch (url.pathname) {
			case '/opener':
				return html(
					'<p>opener</p>',
					'<script>window.addEventListener("message", (e) => { document.title = "got:" + e.data })</script>',
				)
			case '/popup':
				return html('<p>popup</p><script>window.opener.postMessage("hello-from-popup", "*"); window.close()</script>')
			case '/file.bin':
				res.setHeader('content-type', 'application/octet-stream')
				res.setHeader('content-disposition', 'attachment; filename="report.bin"')
				return void res.end(Buffer.alloc(2048, 7))
			case '/find':
				return html('<p>needle one</p><p>needle two</p><p>needle three</p><p>hay</p>')
			case '/article':
				return html(
					`<nav><a href="/other">Site menu</a></nav><article><h1>Fixture Headline</h1><p>${PARAGRAPH}</p><p>${PARAGRAPH}</p></article><footer>Footer junk</footer>`,
					'',
					'Fixture Headline',
				)
			case '/size-a':
				return html('<p>a</p><a id="next" href="/size-b">next</a>')
			case '/size-b':
				return html('<p>b</p>')
			case '/perm':
				return html('<p>perm</p>')
			default:
				res.statusCode = 404
				res.end('not found')
		}
	})
	await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve))
	base = `http://127.0.0.1:${(server.address() as AddressInfo).port}`
})

test.afterAll(() => {
	server.close()
})

async function activeTabId(chrome: Page): Promise<string> {
	return chrome.evaluate(async () => {
		const state = await window.zing.getTabs()
		if (!state.activeId) throw new Error('no active tab')
		return state.activeId
	})
}

async function startNavigation(chrome: Page, url: string): Promise<void> {
	const id = await activeTabId(chrome)
	await chrome.evaluate(([tab, target]) => window.zing.navigateTab(tab!, target!), [id, url])
}

/** Navigates the active tab and waits until a page with that URL has loaded. */
async function openUrl(app: ElectronApplication, chrome: Page, url: string): Promise<void> {
	await startNavigation(chrome, url)
	await expect
		.poll(() =>
			app.evaluate(
				({ webContents }, target) =>
					webContents.getAllWebContents().some((c) => c.getURL() === target && !c.isLoading()),
				url,
			),
		)
		.toBe(true)
}

/** Runs a script in the first page WebContents whose URL contains `part`. */
async function inPage<T>(app: ElectronApplication, part: string, code: string): Promise<T> {
	return app.evaluate(
		async ({ webContents }, [needle, source]) => {
			const contents = webContents.getAllWebContents().find((c) => c.getURL().includes(needle!))
			if (!contents) throw new Error(`no page with ${needle}`)
			return contents.executeJavaScript(source!, true)
		},
		[part, code],
	)
}

async function pageUi(app: ElectronApplication): Promise<Page> {
	const isPageUi = (p: Page): boolean => p.url().endsWith('#pageui')
	const found = app.windows().find(isPageUi)
	if (found) return found
	return app.waitForEvent('window', { predicate: isPageUi })
}

test('opening and navigating a tab raises no main-process exception', async () => {
	const { app, chrome } = await launchZing()
	await app.evaluate(() => {
		const g = globalThis as unknown as { __errors: string[] }
		g.__errors = []
		process.on('uncaughtException', (error) => g.__errors.push(String(error)))
	})
	await chrome.evaluate(() => window.zing.runCommand('tab.new'))
	await openUrl(app, chrome, `${base}/find`)
	await expect.poll(() => app.evaluate(() => (globalThis as unknown as { __errors: string[] }).__errors)).toEqual([])
	await expect(chrome.locator('[role="tab"]')).toHaveCount(2)
	await app.close()
})

test('a popup keeps window.opener, delivers its message and closes back to the opener', async () => {
	const { app, chrome } = await launchZing()
	await app.evaluate(() => {
		const g = globalThis as unknown as { __errors: string[] }
		g.__errors = []
		process.on('uncaughtException', (error) => g.__errors.push(String(error)))
	})
	await openUrl(app, chrome, `${base}/opener`)
	const openerId = await activeTabId(chrome)
	await expect.poll(() => inPage<string>(app, '/opener', 'document.title')).toBe('/opener')
	await inPage(app, '/opener', 'void window.open("/popup")')
	await expect.poll(() => inPage<string>(app, '/opener', 'document.title')).toBe('got:hello-from-popup')
	await expect(chrome.locator('[role="tab"]')).toHaveCount(1)
	expect(await activeTabId(chrome)).toBe(openerId)
	// The popup closed itself while it was the shown page; tearing its view down must not throw.
	expect(await app.evaluate(() => (globalThis as unknown as { __errors: string[] }).__errors)).toEqual([])
	await app.close()
})

test('a download lands in the downloads folder with a Mark of the Web and numbers on collision', async () => {
	const directory = mkdtempSync(join(tmpdir(), 'zing-dl-'))
	process.env['ZING_DOWNLOADS_DIR'] = directory
	const { app, chrome } = await launchZing()
	try {
		await startNavigation(chrome, `${base}/file.bin`)
		const file = join(directory, 'report.bin')
		await expect.poll(() => existsSync(file)).toBe(true)
		await expect.poll(() => existsSync(`${file}:Zone.Identifier`) && readFileSync(`${file}:Zone.Identifier`, 'utf8')).toContain('ZoneId=3')
		const mark = readFileSync(`${file}:Zone.Identifier`, 'utf8')
		expect(mark).toContain(`HostUrl=${base}/file.bin`)
		expect(mark).toContain('ReferrerUrl=')

		const ui = await pageUi(app)
		await expect(ui.getByRole('button', { name: 'Open' })).toBeVisible()
		await expect(ui.getByRole('button', { name: 'Show in folder' })).toBeVisible()

		await startNavigation(chrome, `${base}/file.bin`)
		await expect.poll(() => readdirSync(directory).sort()).toEqual(['report 2.bin', 'report.bin'])
	} finally {
		delete process.env['ZING_DOWNLOADS_DIR']
		await app.close()
	}
})

test('find in page shows a match count and steps through matches', async () => {
	const { app, chrome } = await launchZing()
	await openUrl(app, chrome, `${base}/find`)
	await chrome.evaluate(() => window.zing.runCommand('page.find'))
	const ui = await pageUi(app)
	await ui.getByRole('textbox', { name: 'Find in page' }).fill('needle')
	await expect(ui.getByText('1 of 3')).toBeVisible()
	await chrome.evaluate(() => window.zing.runCommand('page.findNext'))
	await expect(ui.getByText('2 of 3')).toBeVisible()
	await chrome.evaluate(() => window.zing.runCommand('page.findPrevious'))
	await expect(ui.getByText('1 of 3')).toBeVisible()
	await app.close()
})

test('reader mode shows the extracted article in a themed view', async () => {
	const { app, chrome } = await launchZing()
	await openUrl(app, chrome, `${base}/article`)
	await chrome.evaluate(() => window.zing.runCommand('page.reader'))
	await expect
		.poll(() => inPage<boolean>(app, '/article', '!!document.querySelector("main[data-zing-reader]")'))
		.toBe(true)
	const view = await inPage<{ headline: string; text: string; token: string; background: string }>(
		app,
		'/article',
		`(() => {
			const root = getComputedStyle(document.documentElement)
			return {
				headline: document.querySelector('main h1').textContent,
				text: document.body.innerText,
				token: root.getPropertyValue('--bg').trim(),
				background: getComputedStyle(document.body).backgroundColor,
			}
		})()`,
	)
	expect(view.headline).toBe('Fixture Headline')
	expect(view.text).toContain('reader has to build its view')
	expect(view.text).not.toContain('Footer junk')
	expect(view.text).not.toContain('Site menu')
	expect(view.token).toMatch(/^#[0-9a-f]{6}$/i)
	await app.close()
})

test('the text size override persists across the tab navigations', async () => {
	const { app, chrome } = await launchZing()
	const fontSize = (part: string): Promise<string> =>
		inPage<string>(app, part, 'getComputedStyle(document.documentElement).fontSize').catch(() => 'no page')
	await openUrl(app, chrome, `${base}/size-a`)
	await expect.poll(() => fontSize('/size-a')).toBe('16px')
	await chrome.evaluate(() => window.zing.runCommand('page.textBigger'))
	await expect.poll(() => fontSize('/size-a')).toBe('17.6px')
	await inPage(app, '/size-a', 'document.getElementById("next").click()')
	await expect.poll(() => fontSize('/size-b')).toBe('17.6px')
	await app.close()
})

test('permission requests are denied by default and only granted when allowed', async () => {
	const { app, chrome } = await launchZing()
	await openUrl(app, chrome, `${base}/perm`)

	// Not one of the four promptable permissions: refused without asking.
	const midi = await inPage<string>(
		app,
		'/perm',
		'Promise.race([navigator.requestMIDIAccess().then(() => "granted", (e) => e.name), new Promise((r) => setTimeout(() => r("timeout"), 3000))])',
	)
	expect(midi).not.toBe('granted')

	await inPage(app, '/perm', 'window.__result = "pending"; void Notification.requestPermission().then((r) => { window.__result = r })')
	const ui = await pageUi(app)
	await expect(ui.getByText('wants to show notifications')).toBeVisible()
	expect(await inPage<string>(app, '/perm', 'window.__result')).toBe('pending')
	await ui.getByRole('button', { name: 'Deny' }).click()
	await expect.poll(() => inPage<string>(app, '/perm', 'window.__result')).toBe('denied')
	await app.close()
})

test('HTML fullscreen gives the page the whole window and restores afterwards', async () => {
	const { app, chrome } = await launchZing()
	await openUrl(app, chrome, `${base}/find`)
	const windowFullscreen = (): Promise<boolean> =>
		app.evaluate(({ BrowserWindow }) => BrowserWindow.getAllWindows().some((w) => w.isFullScreen()))
	await inPage(app, '/find', 'void document.documentElement.requestFullscreen()')
	await expect.poll(windowFullscreen).toBe(true)
	await expect.poll(() => inPage<boolean>(app, '/find', '!!document.fullscreenElement')).toBe(true)
	await inPage(app, '/find', 'void document.exitFullscreen()')
	await expect.poll(windowFullscreen).toBe(false)
	await app.close()
})

test('the find bar sits below the Favourites bar when it is showing', async () => {
	const { app, chrome } = await launchZing({ settings: { showFavouritesBar: true } })
	await openUrl(app, chrome, `${base}/find`)
	await chrome.evaluate(() => window.zing.runCommand('page.find'))
	await pageUi(app)
	const findTop = () => app.evaluate(({ BrowserWindow }) => {
		const views = BrowserWindow.getAllWindows()[0]!.contentView.children as Electron.WebContentsView[]
		return views.find((v) => v.webContents.getURL().endsWith('#pageui'))?.getBounds().y
	})
	// Tab strip 40 + toolbar 40 + Favourites bar 28, then the 8px margin.
	await expect.poll(findTop).toBe(116)
	await app.close()
})
