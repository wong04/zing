import { createServer, type Server } from 'node:http'
import type { AddressInfo } from 'node:net'
import { existsSync, readdirSync, readFileSync } from 'node:fs'
import { join } from 'node:path'
import { expect, type Locator, type Page } from '@playwright/test'
import { drawFixture, encodePng } from '../src/shared/convert/bgFixture.node'
import type { ZingApi } from '../src/shared/ipc'
import { launchZing, pageWhere, test, type Launched } from './zing'

declare const window: { readonly zing: ZingApi; readonly screenX: number; readonly screenY: number }

// An image dragged out of a page view onto a tool panel on the surface view. CDP can't drive a real OS drag from one WebView2
// window to another, so the drag is made in two real halves: the page's own drag is started with the mouse and caught as it
// leaves (Input.setInterceptDrags gives exactly what Chromium put in it: text/uri-list and text/html, never a file), and
// that data is dropped on the tool's drop zone in the surface (Input.dispatchDragEvent). What the OS carries between the two
// windows is left to a by-hand check (PARITY L15).

type DragData = { items: { mimeType: string; data: string }[]; dragOperationsMask: number }

let server: Server
let port = 0
const png = encodePng(drawFixture(64, 48).rgba, 64, 48)

test.beforeAll(async () => {
	server = createServer((req, res) => {
		if (req.url === '/cat.png') {
			res.setHeader('content-type', 'image/png')
			return void res.end(png)
		}
		res.setHeader('content-type', 'text/html')
		if (req.url === '/notes') return void res.end('<!doctype html><title>Notes</title>just words')
		res.end('<!doctype html><title>Pics</title><body style="margin:0"><img id="plain" src="/cat.png" width="200" height="150"><br><a id="link" href="/notes">notes</a><br><a href="/notes"><img id="linked" src="/cat.png" width="200" height="150"></a></body>')
	})
	await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve))
	port = (server.address() as AddressInfo).port
})

test.afterAll(() => new Promise<void>((resolve) => server.close(() => resolve())))

async function openPics(zing: Launched): Promise<Page> {
	const { activeId } = await zing.chrome.evaluate(() => window.zing.getTabs())
	await zing.chrome.evaluate(([id, to]) => window.zing.navigateTab(id!, to!), [activeId!, `http://127.0.0.1:${port}/`])
	const page = await pageWhere(zing.browser, (p) => p.url().startsWith(`http://127.0.0.1:${port}/`))
	await page.waitForLoadState('load')
	return page
}

/** Starts a real mouse drag on `selector` in the page and returns what the drag carries as it would leave the page. */
async function dragOutOf(zing: Launched, page: Page, selector: string): Promise<DragData> {
	const cdp = await zing.chrome.context().newCDPSession(page)
	await cdp.send('Input.setInterceptDrags', { enabled: true })
	const box = (await page.locator(selector).boundingBox())!
	const x = box.x + box.width / 2
	const y = box.y + box.height / 2
	const caught = new Promise<DragData>((resolve) => cdp.once('Input.dragIntercepted', (event) => resolve(event.data as DragData)))
	await cdp.send('Input.dispatchMouseEvent', { type: 'mousePressed', x, y, button: 'left', clickCount: 1, buttons: 1 })
	for (let step = 1; step <= 10; step++) await cdp.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: x + step * 5, y: y + step * 5, button: 'left', buttons: 1 })
	const data = await caught
	await cdp.send('Input.dispatchMouseEvent', { type: 'mouseReleased', x: x + 50, y: y + 50, button: 'left', clickCount: 1, buttons: 0 })
	await cdp.detach()
	return data
}

/**
 * Drops `data` on the part of `zone` the surface shows. The surface view ends at the screen's edge, so a panel opened near it
 * (as on CI's smaller screen) is cut off, and its zone's centre can lie outside the view, where a drop reaches nothing.
 */
async function dropOn(zing: Launched, surface: Page, zone: Locator, data: DragData): Promise<void> {
	const box = (await zone.boundingBox())!
	const view = await surface.evaluate(() => ({ width: innerWidth, height: innerHeight }))
	const left = Math.max(box.x, 0)
	const right = Math.min(box.x + box.width, view.width)
	const top = Math.max(box.y, 0)
	const bottom = Math.min(box.y + box.height, view.height)
	if (right - left < 1 || bottom - top < 1) throw new Error(`The drop zone (${JSON.stringify(box)}) is outside the surface view (${view.width}x${view.height})`)
	const at = { x: (left + right) / 2, y: (top + bottom) / 2 }
	const cdp = await zing.chrome.context().newCDPSession(surface)
	for (const type of ['dragEnter', 'dragOver', 'drop'] as const) await cdp.send('Input.dispatchDragEvent', { type, ...at, data })
	await cdp.detach()
}

async function openPanel(zing: Launched, id: string, title: string, x: number): Promise<{ surface: Page; panel: Locator }> {
	await zing.chrome.evaluate(([panel, at]) => window.zing.openPanel(panel!, { x: window.screenX + Number(at), y: window.screenY + 120 }), [id, String(x)])
	const surface = await pageWhere(zing.browser, (p) => p.url().endsWith('#surface'))
	const panel = surface.getByRole('dialog', { name: title, exact: true })
	await expect(panel).toBeVisible()
	return { surface, panel }
}

/** The temporary folders fetched page images are kept in. */
const dropFolders = (zing: Launched): string[] => {
	const root = join(zing.userDataDir, 'viewer-copies')
	return existsSync(root) ? readdirSync(root).filter((name) => /^\d+_drop/.test(name)).map((name) => join(root, name)) : []
}

test('an image dragged out of a page loads into Background Remover and into Convert, and its copy goes with the window', async () => {
	const zing = await launchZing()
	const page = await openPics(zing)
	const dragged = await dragOutOf(zing, page, '#plain')
	// What the page gives a drop: the image's address and markup, and no file.
	expect(dragged.items.map((item) => item.mimeType).sort()).toEqual(['text/html', 'text/uri-list'])

	const remover = await openPanel(zing, 'tool:Background Remover', 'Background Remover', 300)
	await dropOn(zing, remover.surface, remover.panel.locator('.convert-drop'), dragged)
	await expect(remover.panel.getByText('cat.png', { exact: true })).toBeVisible()
	await expect(remover.panel.getByText('Drop a picture here')).toBeHidden()

	const convert = await openPanel(zing, 'section:Convert', 'Convert', 920)
	await dropOn(zing, convert.surface, convert.panel.locator('.convert-drop'), dragged)
	await expect(convert.panel.getByText('cat.png', { exact: true })).toBeVisible()
	// It is the picture itself, byte for byte, in a temporary folder of this Zing's own.
	const folders = dropFolders(zing)
	expect(folders).toHaveLength(2)
	for (const folder of folders) expect(readFileSync(join(folder, 'cat.png')).equals(png)).toBe(true)

	await zing.close()
	expect(dropFolders(zing)).toEqual([])
})

test('an image inside a link is taken as the image, not the link', async () => {
	const zing = await launchZing()
	const dragged = await dragOutOf(zing, await openPics(zing), '#linked')
	expect(dragged.items.find((item) => item.mimeType === 'text/uri-list')?.data).toBe(`http://127.0.0.1:${port}/notes`)
	const { surface, panel } = await openPanel(zing, 'tool:Background Remover', 'Background Remover', 300)
	await dropOn(zing, surface, panel.locator('.convert-drop'), dragged)
	await expect(panel.getByText('cat.png', { exact: true })).toBeVisible()
	await zing.close()
})

test('a link to a page that is not a picture is refused in plain words, and nothing is kept', async () => {
	const zing = await launchZing()
	const dragged = await dragOutOf(zing, await openPics(zing), '#link')
	const { surface, panel } = await openPanel(zing, 'section:Convert', 'Convert', 300)
	await dropOn(zing, surface, panel.locator('.convert-drop'), dragged)
	await expect(panel.getByText(/That isn't a picture Zing can take/)).toBeVisible()
	await expect(panel.getByText('Drop a file here', { exact: false })).toBeVisible()
	expect(dropFolders(zing)).toEqual([])
	await zing.close()
})
