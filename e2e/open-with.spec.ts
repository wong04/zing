// Phase 8: files Windows' Open with hands Zing (ZingPDF, ZingHTML). A PDF opens in the Files viewer, an HTML file in a tab at its
// file: address, whether Zing was closed (the path is a launch argument) or already running (a second launch hands it over).
import { mkdtempSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { pathToFileURL } from 'node:url'
import { expect, type Page } from '@playwright/test'
import type { ZingApi } from '../src/shared/ipc'
import { launchZing, pageWhere, test, type Launched } from './zing'

declare const window: { readonly zing: ZingApi }

const PDF_BYTES = '%PDF-1.1\n1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj\n2 0 obj<</Type/Pages/Kids[3 0 R]/Count 1>>endobj\n3 0 obj<</Type/Page/Parent 2 0 R/MediaBox[0 0 200 100]>>endobj\ntrailer<</Root 1 0 R>>\n%%EOF\n'

function files(): { pdf: string; html: string } {
	// A space in the folder, as in a real Documents path: the command line quotes it.
	const work = mkdtempSync(join(tmpdir(), 'zing open with-'))
	const pdf = join(work, 'report.pdf')
	const html = join(work, 'page.html')
	writeFileSync(pdf, PDF_BYTES)
	writeFileSync(html, '<!doctype html><title>Opened from Explorer</title><p>a local page</p>')
	return { pdf, html }
}

const urls = async (chrome: Page): Promise<string[]> => (await chrome.evaluate(() => window.zing.getTabs())).tabs.map((t) => t.url)

async function expectPdfShown(zing: Launched, name: string): Promise<void> {
	const surface = await pageWhere(zing.browser, (p) => p.url().endsWith('#surface'))
	const panel = surface.getByRole('dialog', { name: 'Files' })
	await expect(panel.getByTestId('viewer-pdf')).toBeVisible()
	await expect(panel.locator('.viewer-name')).toHaveText(name)
}

test('a PDF in the launch arguments opens in the Files viewer', async () => {
	const { pdf } = files()
	const zing = await launchZing({ args: [pdf] })
	await expectPdfShown(zing, 'report.pdf')
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
	await expectPdfShown(zing, 'report.pdf')
	expect(await zing.launchAgain([html])).toBe(0)
	await expect.poll(() => urls(zing.chrome)).toContain(pathToFileURL(html).href)
	await zing.close()
})

test('a path that is not an existing PDF or HTML file opens nothing', async () => {
	const { pdf } = files()
	const zing = await launchZing({ args: [pdf.replace('report.pdf', 'missing.pdf'), 'C:\\Windows\\win.ini', 'report.pdf'] })
	await zing.chrome.waitForTimeout(2000)
	expect((await urls(zing.chrome)).map((u) => u.replace(/\/$/, ''))).toEqual(['zing://home'])
	expect((await zing.chrome.evaluate(() => window.zing.getToolPanels())).panels).toEqual([])
	await zing.close()
})
