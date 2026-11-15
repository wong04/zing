// Phase 8: files Windows' Open with hands Zing (ZingPDF, ZingHTML). Both open in a tab at their file: address, a PDF in the engine's
// own viewer, whether Zing was closed (the path is a launch argument) or already running (a second launch hands it over).
import { mkdtempSync, realpathSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { pathToFileURL } from 'node:url'
import { expect, type Page } from '@playwright/test'
import type { ZingApi } from '../src/shared/ipc'
import { exitFullScreenButton, launchZing, layers, pageWhere, setCursor, test, type Launched } from './zing'

declare const window: { readonly zing: ZingApi }

const PDF_BYTES = '%PDF-1.1\n1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj\n2 0 obj<</Type/Pages/Kids[3 0 R]/Count 1>>endobj\n3 0 obj<</Type/Page/Parent 2 0 R/MediaBox[0 0 200 100]>>endobj\ntrailer<</Root 1 0 R>>\n%%EOF\n'

function files(): { pdf: string; html: string } {
	// A space in the folder, as in a real Documents path: the command line quotes it.
	// The long form of the temp folder: CI's is an 8.3 name (RUNNER~1), whose ~ Node's file URLs escape and Chromium's don't.
	const work = realpathSync.native(mkdtempSync(join(tmpdir(), 'zing open with-')))
	const pdf = join(work, 'report.pdf')
	const html = join(work, 'page.html')
	writeFileSync(pdf, PDF_BYTES)
	writeFileSync(html, '<!doctype html><title>Opened from Explorer</title><p>a local page</p>')
	return { pdf, html }
}

const urls = async (chrome: Page): Promise<string[]> => (await chrome.evaluate(() => window.zing.getTabs())).tabs.map((t) => t.url)

const panels = async (chrome: Page): Promise<readonly unknown[]> => (await chrome.evaluate(() => window.zing.getToolPanels())).panels

/** The PDF is the tab's page, drawn by the engine's PDF viewer, and the Files viewer stays shut. */
async function expectPdfTab(zing: Launched, pdf: string): Promise<Page> {
	const url = pathToFileURL(pdf).href
	await expect.poll(() => urls(zing.chrome)).toContain(url)
	const page = await pageWhere(zing.browser, (p) => p.url() === url)
	await expect.poll(() => page.evaluate(() => (globalThis as unknown as { document: { contentType: string } }).document.contentType)).toBe('application/pdf')
	await expect(page.locator('embed[type="application/pdf"]')).toHaveCount(1)
	expect(await panels(zing.chrome)).toEqual([])
	return page
}

test('a PDF in the launch arguments opens in the blank tab at its file address', async () => {
	const { pdf } = files()
	const zing = await launchZing({ args: [pdf] })
	await expectPdfTab(zing, pdf)
	expect(await urls(zing.chrome)).toEqual([pathToFileURL(pdf).href])
	await zing.close()
})

/**
 * The engine's PDF viewer: a guest view (a `webview` target whose parent is the tab's page), not a frame of the page, so
 * Playwright doesn't list it. Its own toolbar button is pressed over the browser's DevTools session.
 */
async function pdfViewer(zing: Launched, page: Page) {
	const cdp = await zing.browser.newBrowserCDPSession()
	const own = await page.context().newCDPSession(page)
	const { targetInfo } = await own.send('Target.getTargetInfo')
	await own.detach()
	type Target = { targetId: string; type: string; parentId?: string }
	const find = async () => ((await cdp.send('Target.getTargets')).targetInfos as Target[]).find((t) => t.type === 'webview' && t.parentId === targetInfo.targetId)
	await expect.poll(async () => (await find()) !== undefined).toBe(true)
	const { sessionId } = await cdp.send('Target.attachToTarget', { targetId: (await find())!.targetId, flatten: false })
	let next = 1
	const send = async <T>(method: string, params: object): Promise<T> => {
		const id = next++
		const answer = new Promise<T>((resolve, reject) => {
			const listener = ({ sessionId: from, message }: { sessionId: string; message: string }) => {
				const reply = JSON.parse(message) as { id?: number; result?: T; error?: { message: string } }
				if (from !== sessionId || reply.id !== id) return
				cdp.off('Target.receivedMessageFromTarget', listener)
				if (reply.error) reject(new Error(`${method}: ${reply.error.message}`))
				else resolve(reply.result!)
			}
			cdp.on('Target.receivedMessageFromTarget', listener)
		})
		await cdp.send('Target.sendMessageToTarget', { sessionId, message: JSON.stringify({ id, method, params }) })
		return answer
	}
	const evaluate = async <T>(expression: string): Promise<T> => (await send<{ result: { value: T } }>('Runtime.evaluate', { expression, returnByValue: true })).result.value
	await expect.poll(() => evaluate<boolean>('document.getElementById("full-screen") !== null')).toBe(true)
	return {
		fullScreen: () => evaluate<boolean>('document.fullscreenElement !== null'),
		// A click on the button as a user gesture, which the viewer's requestFullscreen needs. Input.dispatchMouseEvent at the
		// button's centre never reached it in the off-screen test window.
		press: async (id: string) => {
			await send('Runtime.evaluate', { expression: `document.getElementById(${JSON.stringify(id)}).click()`, userGesture: true })
		},
	}
}

test("a PDF that the viewer's own button makes fullscreen takes the whole window, and Zing's button gives the chrome back", async () => {
	const { pdf } = files()
	const zing = await launchZing({ args: [pdf] })
	const page = await expectPdfTab(zing, pdf)
	const url = pathToFileURL(pdf).href
	const shown = async () => (await layers(zing.chrome)).find((l) => l.url === url && l.visible)
	const inChrome = await shown()
	expect(inChrome!.y).toBeGreaterThan(0)
	const viewer = await pdfViewer(zing, page)
	await viewer.press('full-screen')
	await expect.poll(viewer.fullScreen).toBe(true)
	await expect.poll(async () => (await shown())?.y).toBe(0)
	expect((await shown())!.height).toBeGreaterThan(inChrome!.height)
	const overlay = await pageWhere(zing.browser, (p) => p.url().endsWith('#overlay'))
	await expect(overlay.getByRole('status')).toHaveText('This page is now full screen · Press Esc to exit')
	// The viewer's own fullscreen has no way out on screen, so Zing's button at the top edge is it.
	await setCursor(zing.chrome, { x: (await shown())!.width / 2, y: 0 })
	await (await exitFullScreenButton(zing.browser)).click()
	await expect.poll(viewer.fullScreen).toBe(false)
	await expect.poll(async () => (await shown())?.y).toBe(inChrome!.y)
	await setCursor(zing.chrome, null)
	await zing.close()
})

test("Esc takes a PDF out of the viewer's own fullscreen", async () => {
	const { pdf } = files()
	const zing = await launchZing({ args: [pdf] })
	const page = await expectPdfTab(zing, pdf)
	const url = pathToFileURL(pdf).href
	const shown = async () => (await layers(zing.chrome)).find((l) => l.url === url && l.visible)
	const inChrome = await shown()
	const viewer = await pdfViewer(zing, page)
	await viewer.press('full-screen')
	await expect.poll(async () => (await shown())?.y).toBe(0)
	// A real Esc as the page's controller reports it (AcceleratorKeyPressed), which CDP input never reaches: the same handler.
	await zing.chrome.evaluate(() => (globalThis as unknown as { chrome: { webview: { postMessage(m: unknown): void } } }).chrome.webview.postMessage({ kind: 'test', action: 'fullscreen-key', vk: 0x1b }))
	await expect.poll(viewer.fullScreen).toBe(false)
	await expect.poll(async () => (await shown())?.y).toBe(inChrome!.y)
	await zing.close()
})

test('an HTML file in the launch arguments opens in the blank tab at its file address', async () => {
	const { html } = files()
	const zing = await launchZing({ args: [html] })
	await expect.poll(() => urls(zing.chrome)).toEqual([pathToFileURL(html).href])
	await zing.close()
})

test('a running Zing takes a PDF and an HTML file from a second launch', async () => {
	const { pdf, html } = files()
	const zing = await launchZing()
	expect(await zing.launchAgain([pdf])).toBe(0)
	await expectPdfTab(zing, pdf)
	expect(await zing.launchAgain([html])).toBe(0)
	await expect.poll(() => urls(zing.chrome)).toContain(pathToFileURL(html).href)
	await zing.close()
})

test('a path that is not an existing PDF or HTML file opens nothing', async () => {
	const { pdf } = files()
	const zing = await launchZing({ args: [pdf.replace('report.pdf', 'missing.pdf'), 'C:\\Windows\\win.ini', 'report.pdf'] })
	await zing.chrome.waitForTimeout(2000)
	expect((await urls(zing.chrome)).map((u) => u.replace(/\/$/, ''))).toEqual(['zing://home'])
	expect(await panels(zing.chrome)).toEqual([])
	await zing.close()
})
