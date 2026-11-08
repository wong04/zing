// Link previews (docs/superpowers/specs/2026-11-08-link-preview-design.md): a link to another site opens over the tab,
// on local fixtures served on two hosts, 127.0.0.1 and localhost, so "another site" needs no live one.
import { existsSync, statSync } from 'node:fs'
import { createServer, type Server, type ServerResponse } from 'node:http'
import type { AddressInfo } from 'node:net'
import { join } from 'node:path'
import { expect, type Page } from '@playwright/test'
import type { ZingApi } from '../src/shared/ipc'
import { launchZing, test, layers, liveCounts, pageWhere, peekState, setModifiers, type Launched, type Layer } from './zing'

// Evaluated in the chrome renderer, where api.ts puts the bridge on window.
declare const window: { readonly zing: ZingApi }

let server: Server
let own = ''
let other = ''
/** Ends the slow download's body, which the server holds open until then. */
let finishSlow: (() => void) | undefined

/** A body that waits: the download is still going when the test acts. */
function holdSlow(res: ServerResponse): void {
	res.setHeader('content-type', 'application/octet-stream')
	res.setHeader('content-disposition', 'attachment; filename="slow.bin"')
	res.setHeader('content-length', '2048')
	res.write(Buffer.alloc(1024, 3))
	finishSlow = () => res.end(Buffer.alloc(1024, 3))
}

test.beforeAll(async () => {
	server = createServer((req, res) => {
		// The same pages on both hosts; each links to the other one as "another site".
		const host = req.headers.host ?? ''
		const port = host.slice(host.lastIndexOf(':') + 1)
		const elsewhere = host.startsWith('127.0.0.1') ? `http://localhost:${port}` : `http://127.0.0.1:${port}`
		const name = (req.url ?? '/').slice(1) || 'home'
		if (name === 'file.bin') {
			res.setHeader('content-type', 'application/octet-stream')
			res.setHeader('content-disposition', 'attachment; filename="report.bin"')
			return void res.end(Buffer.alloc(2048, 7))
		}
		if (name === 'slow.bin') return holdSlow(res)
		res.setHeader('content-type', 'text/html')
		if (name === 'a')
			return void res.end(`<!doctype html><title>Page a</title>
<a id="cross" href="${elsewhere}/b">cross</a>
<a id="same" href="/b">same</a>
<a id="to-opener" href="${elsewhere}/opener">opener</a>
<a id="blank" href="${elsewhere}/b" target="_blank">blank</a>
<button id="open" onclick="window.open('${elsewhere}/b')">open</button>
<button id="open-same" onclick="window.open('/b')">open same</button>
<button id="open-blank" onclick="window.open('about:blank').location = '${elsewhere}/b'">open blank</button>`)
		if (name === 'b')
			return void res.end(`<!doctype html><title>Page b</title><body style="height:3000px">
<input id="field" aria-label="field">
<a id="file" href="/file.bin">file</a>
<a id="slow" href="/slow.bin">slow</a>
</body>`)
		if (name === 'opener')
			return void res.end(`<!doctype html><title>Opener</title><button onclick="window.popup = window.open('/popup')">open</button>
<button id="full" onclick="document.documentElement.requestFullscreen()">full</button>`)
		// Closes itself when its opener asks.
		if (name === 'popup') return void res.end('<!doctype html><title>Popup</title><script>document.title = window.opener ? "Popup with opener" : "Popup alone"; addEventListener("message", (e) => e.data === "close" && window.close())</script>')
		res.end(`<!doctype html><title>Page ${name}</title><p>${name}</p>`)
	})
	await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve))
	const port = (server.address() as AddressInfo).port
	own = `http://127.0.0.1:${port}`
	other = `http://localhost:${port}`
})

test.afterAll(() => new Promise<void>((resolve) => server.close(() => resolve())))

const tabsOf = (chrome: Page) => chrome.evaluate(() => window.zing.getTabs())
const tabCount = async (chrome: Page) => (await tabsOf(chrome)).tabs.length
const run = (chrome: Page, id: string) => chrome.evaluate((command) => window.zing.runCommand(command as never), id)

/** Zing on /a of the first host, with the page loaded. */
async function start(): Promise<{ zing: Launched; source: Page }> {
	const zing = await launchZing({ env: { ZING_START_URL: `${own}/a` } })
	const source = await pageWhere(zing.browser, (p) => p.url() === `${own}/a`)
	await source.waitForLoadState('load')
	return { zing, source }
}

/** Waits until the preview's only layer is on this address. */
async function previewOf(zing: Launched, url: string): Promise<void> {
	await expect.poll(async () => (await peekState(zing.chrome)).layers.map((l) => l.url)).toEqual([url])
}

/**
 * A preview page Zing made itself (a plain link click), with its page loaded. A page the engine made for window.open is
 * never listed to Playwright over CDP, so those are checked through the host and their opener.
 */
async function previewPage(zing: Launched, url: string): Promise<Page> {
	await previewOf(zing, url)
	const page = await pageWhere(zing.browser, (p) => p.url() === url)
	await page.waitForLoadState('load')
	return page
}

interface Box {
	readonly x: number
	readonly y: number
	readonly width: number
	readonly height: number
}

/** PeekLayout.Place (host/Zing.Core/LinkPreview/PeekLayout.cs): a layer's page and the buttons beside it, in DIPs. */
function place(area: Box, depth: number): { page: Box; buttons: Box } {
	const layerInset = 16 * depth
	const side = Math.max(area.width * 0.06, 48) + layerInset
	const width = Math.min(area.width - 2 * side, 1400)
	const page = { x: area.x + (area.width - width) / 2, y: area.y + 24 + layerInset, width, height: area.height - 2 * (24 + layerInset) }
	return { page, buttons: { x: page.x + page.width + 8, y: page.y, width: 32, height: 32 * 2 + 8 } }
}

const boxOf = ({ x, y, width, height }: Box): Box => ({ x, y, width, height })

/** Each edge within 1 DIP: the host rounds to pixels. */
function expectNear(actual: Box, expected: Box, what: string): void {
	for (const key of ['x', 'y', 'width', 'height'] as const) expect(Math.abs(actual[key] - expected[key]), `${what} ${key}: ${actual[key]} against ${expected[key]}`).toBeLessThanOrEqual(1)
}

/** The visible view on this address, once it shows. */
async function shown(chrome: Page, match: (layer: Layer) => boolean): Promise<Layer> {
	let found: Layer | undefined
	await expect.poll(async () => (found = (await layers(chrome)).find((l) => match(l) && l.visible)) !== undefined).toBe(true)
	return found!
}

interface PreviewLayers {
	readonly preview: Layer
	readonly scrim: Layer
	readonly peek: Layer
}

/** The preview's page, its scrim and its buttons, once all three show; the page area is the source tab's view. */
async function previewShown(chrome: Page, sourceUrl: string, previewUrl: string): Promise<PreviewLayers & { area: Box }> {
	const area = boxOf(await shown(chrome, (l) => l.url === sourceUrl))
	const preview = await shown(chrome, (l) => l.url === previewUrl)
	const scrim = await shown(chrome, (l) => l.url === 'scrim')
	const peek = await shown(chrome, (l) => l.url.endsWith('#peek'))
	return { area, preview, scrim, peek }
}

/** The views of a preview that no longer exists: none of its page, scrim or buttons is on screen. */
async function expectNoPreview(chrome: Page, previewUrl: string): Promise<void> {
	await expect.poll(async () => (await peekState(chrome)).open).toBe(false)
	await expect.poll(async () => (await layers(chrome)).filter((l) => l.visible && (l.url === previewUrl || l.url === 'scrim' || l.url.endsWith('#peek'))).map((l) => l.url)).toEqual([])
}

test('a cross-host link opens a preview, not a tab', async () => {
	const { zing, source } = await start()
	const { activeId } = await tabsOf(zing.chrome)
	const before = await tabCount(zing.chrome)
	await source.click('#cross')
	await previewOf(zing, `${other}/b`)
	expect(await peekState(zing.chrome)).toMatchObject({ open: true, canExpand: true, sourceTabId: activeId })
	expect(await tabCount(zing.chrome)).toBe(before)
	// The tab under it stays where it was.
	expect((await tabsOf(zing.chrome)).tabs.find((t) => t.id === activeId)?.url).toBe(`${own}/a`)

	const { area, preview, scrim, peek } = await previewShown(zing.chrome, `${own}/a`, `${other}/b`)
	const expected = place(area, 0)
	expectNear(preview, expected.page, 'the preview')
	expectNear(scrim, area, 'the scrim')
	expectNear(peek, expected.buttons, 'the buttons')
	// Over the tab's page, under nothing of its own.
	expect(preview.depth).toBeLessThan((await layers(zing.chrome)).find((l) => l.url === `${own}/a`)!.depth)

	// target=_blank and a plain window.open to another site preview too (the Gmail case).
	await run(zing.chrome, 'preview.close')
	await expectNoPreview(zing.chrome, `${other}/b`)
	await source.click('#blank')
	await previewOf(zing, `${other}/b`)
	await run(zing.chrome, 'preview.close')
	await expectNoPreview(zing.chrome, `${other}/b`)
	await source.click('#open')
	await previewOf(zing, `${other}/b`)
	expect(await tabCount(zing.chrome)).toBe(before)
	await zing.close()
})

test('a same-host link navigates in place', async () => {
	const { zing, source } = await start()
	const { activeId } = await tabsOf(zing.chrome)
	const before = await tabCount(zing.chrome)
	await source.click('#same')
	await expect.poll(async () => (await tabsOf(zing.chrome)).tabs.find((t) => t.id === activeId)?.url).toBe(`${own}/b`)
	expect((await peekState(zing.chrome)).open).toBe(false)
	expect(await tabCount(zing.chrome)).toBe(before)
	await zing.close()
})

test('the close button and Ctrl+W close it and leak nothing', async () => {
	const { zing, source } = await start()
	// The buttons' view is made with the first preview and kept for the window; one preview first, so the counts compare like with like.
	await source.click('#cross')
	await previewShown(zing.chrome, `${own}/a`, `${other}/b`)
	await run(zing.chrome, 'preview.close')
	await expectNoPreview(zing.chrome, `${other}/b`)
	const rest = await liveCounts(zing.chrome)
	const closedAndLeftNothing = async () => {
		await expectNoPreview(zing.chrome, `${other}/b`)
		await expect.poll(() => liveCounts(zing.chrome)).toMatchObject({ windows: rest.windows, views: rest.views, pending: 0 })
	}

	await source.click('#cross')
	const { peek } = await previewShown(zing.chrome, `${own}/a`, `${other}/b`)
	expect(peek.visible).toBe(true)
	const buttons = await pageWhere(zing.browser, (p) => p.url().endsWith('#peek'))
	await buttons.getByRole('button', { name: 'Close preview' }).click()
	await closedAndLeftNothing()

	await source.click('#cross')
	await previewShown(zing.chrome, `${own}/a`, `${other}/b`)
	const tabs = await tabCount(zing.chrome)
	await run(zing.chrome, 'tab.close')
	await closedAndLeftNothing()
	// Ctrl+W closed the preview, not the tab under it.
	expect(await tabCount(zing.chrome)).toBe(tabs)
	await zing.close()
})

test('expand keeps the same page', async () => {
	const { zing, source } = await start()
	const { activeId: sourceId, tabs } = await tabsOf(zing.chrome)
	await source.click('#cross')
	const preview = await previewPage(zing, `${other}/b`)
	const { area } = await previewShown(zing.chrome, `${own}/a`, `${other}/b`)
	await preview.evaluate(() => ((globalThis as unknown as { __marker: string }).__marker = 'kept'))
	const timeOrigin = await preview.evaluate(() => performance.timeOrigin)

	expect(await run(zing.chrome, 'preview.expand')).toMatchObject({ status: 'done' })
	await expect.poll(() => tabCount(zing.chrome)).toBe(tabs.length + 1)
	const after = await tabsOf(zing.chrome)
	const expanded = after.tabs[after.tabs.findIndex((t) => t.id === sourceId) + 1]!
	expect(expanded.url).toBe(`${other}/b`)
	expect(after.activeId).toBe(expanded.id)
	// The same document: nothing reloaded.
	expect(await preview.evaluate(() => (globalThis as unknown as { __marker?: string }).__marker)).toBe('kept')
	expect(await preview.evaluate(() => performance.timeOrigin)).toBe(timeOrigin)
	expect((await peekState(zing.chrome)).open).toBe(false)
	await expect.poll(async () => (await layers(zing.chrome)).some((l) => l.url === 'scrim')).toBe(false)
	// In the tab's slot now.
	expectNear(await shown(zing.chrome, (l) => l.url === `${other}/b`), area, 'the expanded tab')

	// Closing the new tab goes back to the tab it came from.
	await zing.chrome.evaluate((id) => window.zing.closeTab(id), expanded.id)
	await expect.poll(async () => (await tabsOf(zing.chrome)).activeId).toBe(sourceId)
	await zing.close()
})

test('a popup from a preview keeps window.opener', async () => {
	const { zing, source } = await start()
	const tabs = await tabCount(zing.chrome)
	await source.click('#to-opener')
	const opener = await previewPage(zing, `${other}/opener`)
	await opener.getByRole('button', { name: 'open', exact: true }).click()
	await expect.poll(async () => (await peekState(zing.chrome)).layers.map((l) => l.url)).toEqual([`${other}/opener`, `${other}/popup`])
	// The same site, so the opener can read its popup's title.
	await expect.poll(() => opener.evaluate(() => (globalThis as unknown as { popup: { document: { title: string } } }).popup.document.title)).toBe('Popup with opener')
	expect((await peekState(zing.chrome)).canExpand).toBe(false)
	expect(await tabCount(zing.chrome)).toBe(tabs)
	// Stacked over the preview, inset one step more.
	const { area, preview } = await previewShown(zing.chrome, `${own}/a`, `${other}/popup`)
	expectNear(preview, place(area, 1).page, 'the popup layer')

	// The popup closes itself (window.close in its own page).
	await opener.evaluate(() => (globalThis as unknown as { popup: { postMessage(m: string, origin: string): void } }).popup.postMessage('close', '*'))
	await expect.poll(async () => (await peekState(zing.chrome)).layers.map((l) => l.url)).toEqual([`${other}/opener`])
	const shownAgain = await previewShown(zing.chrome, `${own}/a`, `${other}/opener`)
	expectNear(shownAgain.preview, place(area, 0).page, 'the preview under the popup')
	expect((await peekState(zing.chrome)).canExpand).toBe(true)
	await zing.close()
})

test('with linkPreviews off a cross-host link navigates in place', async () => {
	const { zing, source } = await start()
	await zing.chrome.evaluate(() => window.zing.setSetting('linkPreviews', false))
	const { activeId } = await tabsOf(zing.chrome)
	const before = await tabCount(zing.chrome)
	await source.click('#cross')
	await expect.poll(async () => (await tabsOf(zing.chrome)).tabs.find((t) => t.id === activeId)?.url).toBe(`${other}/b`)
	expect((await peekState(zing.chrome)).open).toBe(false)
	expect(await tabCount(zing.chrome)).toBe(before)
	await zing.close()
})

test('shift-click previews a same-host link', async () => {
	const { zing, source } = await start()
	const before = await tabCount(zing.chrome)
	await setModifiers(zing.chrome, { shift: true })
	await source.click('#open-same')
	await previewOf(zing, `${own}/b`)
	await previewShown(zing.chrome, `${own}/a`, `${own}/b`)
	expect(await tabCount(zing.chrome)).toBe(before)
	await zing.close()
})

test('a blank window set later becomes a preview', async () => {
	const { zing, source } = await start()
	const before = await tabCount(zing.chrome)
	await source.click('#open-blank')
	await previewOf(zing, `${other}/b`)
	await previewShown(zing.chrome, `${own}/a`, `${other}/b`)
	expect(await tabCount(zing.chrome)).toBe(before)
	await zing.close()
})

test('switching tabs closes the preview and Ctrl+Shift+T reopens it as a tab', async () => {
	const { zing, source } = await start()
	const { activeId: sourceId } = await tabsOf(zing.chrome)
	await run(zing.chrome, 'tab.new')
	const { activeId: otherTab } = await tabsOf(zing.chrome)
	await zing.chrome.evaluate((id) => window.zing.activateTab(id), sourceId!)
	await expect.poll(async () => (await tabsOf(zing.chrome)).activeId).toBe(sourceId)
	await source.click('#cross')
	await previewOf(zing, `${other}/b`)
	const tabs = await tabCount(zing.chrome)

	await zing.chrome.evaluate((id) => window.zing.activateTab(id), otherTab!)
	await expectNoPreview(zing.chrome, `${other}/b`)
	expect(await tabCount(zing.chrome)).toBe(tabs)

	expect(await run(zing.chrome, 'tab.reopenClosed')).toMatchObject({ status: 'done' })
	await expect.poll(() => tabCount(zing.chrome)).toBe(tabs + 1)
	const reopened = (await tabsOf(zing.chrome)).tabs.find((t) => t.url === `${other}/b`)
	expect(reopened, 'the preview came back as a tab').toBeDefined()
	expect(reopened!.title).toBe('Page b')
	expect((await peekState(zing.chrome)).open).toBe(false)
	await zing.close()
})

test('a preview page can go fullscreen and comes back', async () => {
	const { zing, source } = await start()
	await source.click('#to-opener')
	const page = await previewPage(zing, `${other}/opener`)
	const before = await previewShown(zing.chrome, `${own}/a`, `${other}/opener`)
	const content = boxOf(await shown(zing.chrome, (l) => l.url.endsWith('#chrome')))
	const fullscreen = () => page.evaluate(() => document.fullscreenElement !== null)

	// A trusted click: requestFullscreen needs a user gesture.
	await page.click('#full')
	await expect.poll(fullscreen).toBe(true)
	// The preview fills the window; the dim and the buttons step aside.
	await expect.poll(async () => boxOf((await layers(zing.chrome)).find((l) => l.url === `${other}/opener`)!)).toEqual(content)
	await expect.poll(async () => (await layers(zing.chrome)).filter((l) => l.visible && (l.url === 'scrim' || l.url.endsWith('#peek'))).map((l) => l.url)).toEqual([])

	await page.evaluate(() => document.exitFullscreen())
	await expect.poll(fullscreen).toBe(false)
	const after = await previewShown(zing.chrome, `${own}/a`, `${other}/opener`)
	expectNear(after.preview, before.preview, 'the preview after fullscreen')
	expectNear(after.scrim, before.scrim, 'the scrim after fullscreen')
	expectNear(after.peek, before.peek, 'the buttons after fullscreen')

	// A popup opened from the fullscreen preview takes it out of fullscreen, and the chrome comes back.
	await page.click('#full')
	await expect.poll(fullscreen).toBe(true)
	await page.getByRole('button', { name: 'open', exact: true }).click()
	await expect.poll(async () => (await peekState(zing.chrome)).layers.map((l) => l.url)).toEqual([`${other}/opener`, `${other}/popup`])
	await expect.poll(fullscreen).toBe(false)
	const stacked = await previewShown(zing.chrome, `${own}/a`, `${other}/popup`)
	expectNear(stacked.preview, place(before.area, 1).page, 'the popup layer')
	expectNear(boxOf((await layers(zing.chrome)).find((l) => l.url === `${other}/opener`)!), before.preview, 'the preview under the popup')
	expectNear(stacked.scrim, before.scrim, 'the scrim under the popup')
	await zing.close()
})

/** The Downloads folder and what the downloads list says of a file. */
function downloadsOf(zing: Launched) {
	const folder = join(zing.userDataDir, 'downloads')
	return {
		sizeOf: (name: string) => () => existsSync(join(folder, name)) && statSync(join(folder, name)).size,
		statusOf: (name: string) => async () => (await zing.chrome.evaluate(() => window.zing.getPageUi())).downloads.find((d) => d.filename === name)?.status,
	}
}

test('a download started in a preview completes', async () => {
	const { zing, source } = await start()
	const { sizeOf, statusOf } = downloadsOf(zing)
	await source.click('#cross')
	const preview = await previewPage(zing, `${other}/b`)
	await preview.click('#file')
	await expect.poll(sizeOf('report.bin')).toBe(2048)
	await expect.poll(statusOf('report.bin')).toBe('completed')
	// The preview showed a document before the download, so it stays.
	expect((await peekState(zing.chrome)).layers.map((l) => l.url)).toEqual([`${other}/b`])
	await zing.close()
})

test('a download still completes when its preview closes while it comes in', async () => {
	// Known product bug, not the preview's own: the file is written whole, but the downloads list never hears the end once
	// the page that started it is closed (its CoreWebView2 goes, and the operation's StateChanged with it). A tab closed
	// mid-download shows the same. Remove this line when it is fixed.
	test.fail(true, 'the downloads list keeps a download "progressing" after the page that started it closes')
	const { zing, source } = await start()
	const { sizeOf, statusOf } = downloadsOf(zing)
	try {
		await source.click('#cross')
		const preview = await previewPage(zing, `${other}/b`)
		finishSlow = undefined
		await preview.click('#slow')
		await expect.poll(statusOf('slow.bin')).toBe('progressing')
		await run(zing.chrome, 'preview.close')
		await expectNoPreview(zing.chrome, `${other}/b`)
		expect(finishSlow, 'the server is holding the slow body').toBeDefined()
		finishSlow!()
		await expect.poll(sizeOf('slow.bin')).toBe(2048)
		await expect.poll(statusOf('slow.bin')).toBe('completed')
	} finally {
		await zing.close()
	}
})
