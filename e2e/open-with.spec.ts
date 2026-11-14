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

test('a PDF opened from Explorer can take the whole window', async () => {
	const { pdf } = files()
	const zing = await launchZing({ args: [pdf] })
	const page = await expectPdfTab(zing, pdf)
	const url = pathToFileURL(pdf).href
	const shown = async () => (await layers(zing.chrome)).find((l) => l.url === url && l.visible)
	const inChrome = await shown()
	expect(inChrome!.y).toBeGreaterThan(0)
	// Playwright's evaluate counts as a user gesture, which requestFullscreen needs.
	await page.evaluate(() => (globalThis as unknown as { document: { documentElement: { requestFullscreen(): Promise<void> } } }).document.documentElement.requestFullscreen())
	await expect.poll(async () => (await shown())?.y).toBe(0)
	expect((await shown())!.height).toBeGreaterThan(inChrome!.height)
	// The viewer's own fullscreen has no way out on screen, so Zing's button at the top edge is it.
	await setCursor(zing.chrome, { x: (await shown())!.width / 2, y: 0 })
	await (await exitFullScreenButton(zing.browser)).click()
	await expect.poll(async () => (await shown())?.y).toBe(inChrome!.y)
	await setCursor(zing.chrome, null)
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
