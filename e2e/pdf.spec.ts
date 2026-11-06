import { copyFileSync, existsSync, mkdirSync, readdirSync, readFileSync, writeFileSync } from 'node:fs'
import { createServer, type Server } from 'node:http'
import type { AddressInfo } from 'node:net'
import { join } from 'node:path'
import { expect, type Locator, type Page } from '@playwright/test'
import { PDFDocument, PDFName, StandardFonts, rgb } from 'pdf-lib'
import type { ZingApi } from '../src/shared/ipc'
import { launchZing, pageWhere, test, type Launched } from './zing'

declare const window: { readonly zing: ZingApi }

// PDF Tools run on the machine: no engine, no outside address. The fixtures are small PDFs made here with pdf-lib.

let pageServer: Server | undefined

test.afterEach(async () => {
	await new Promise<void>((resolve) => (pageServer ? pageServer.close(() => resolve()) : resolve()))
	pageServer = undefined
})

/** A PDF whose page n is `widths[n-1]` points wide, so a page can be told apart by its size, with something dark drawn on it. */
async function makePdf(path: string, widths: readonly number[]): Promise<string> {
	const doc = await PDFDocument.create()
	const font = await doc.embedFont(StandardFonts.Helvetica)
	widths.forEach((width, i) => {
		const page = doc.addPage([width, 200])
		page.drawRectangle({ x: 10, y: 10, width: width - 20, height: 120, color: rgb(0, 0, 0) })
		page.drawText(`Page ${i + 1}`, { x: 14, y: 150, size: 20, font })
	})
	writeFileSync(path, await doc.save())
	return path
}

const widthsOf = async (path: string): Promise<number[]> => (await PDFDocument.load(readFileSync(path))).getPages().map((p) => Math.round(p.getWidth()))
const downloadsOf = (zing: Launched): string => join(zing.userDataDir, 'downloads')
const filesIn = (dir: string): string[] => (existsSync(dir) ? readdirSync(dir).sort() : [])

async function queuePick(zing: Launched, ...files: string[]): Promise<void> {
	await zing.chrome.evaluate((paths) => (globalThis as unknown as { chrome: { webview: { postMessage(m: unknown): void } } }).chrome.webview.postMessage({ kind: 'test', action: 'pick-files', files: paths }), files)
}

async function openTool(zing: Launched, name: string): Promise<{ surface: Page; panel: Locator }> {
	await zing.chrome.getByRole('button', { name: 'Toolbox' }).first().click()
	const surface = await pageWhere(zing.browser, (p) => p.url().endsWith('#surface'))
	await expect(surface.getByRole('dialog', { name: 'Toolbox' })).toBeVisible()
	await surface.getByTestId('toolbox-tile').getByText(name, { exact: true }).click()
	const panel = surface.getByRole('dialog', { name, exact: true })
	await expect(panel).toBeVisible()
	return { surface, panel }
}

async function openPdf(zing: Launched, ...files: string[]): Promise<{ surface: Page; panel: Locator }> {
	const opened = await openTool(zing, 'PDF Tools')
	await queuePick(zing, ...files)
	await opened.panel.getByRole('button', { name: /^(Choose PDFs|Add another PDF)/ }).click()
	return opened
}

const darkPixels = (surface: Page, index: number): Promise<number> =>
	surface.evaluate((i) => {
		const canvas = document.querySelectorAll<HTMLCanvasElement>('.pdf-thumb-canvas')[i]!
		if (canvas.width === 0) return 0
		const data = canvas.getContext('2d')!.getImageData(0, 0, canvas.width, canvas.height).data
		let dark = 0
		for (let p = 0; p < data.length; p += 4) if (data[p]! < 60 && data[p + 3]! > 200) dark++
		return dark
	}, index)

test('merge two PDFs and the output lands in Downloads with a numbered name', async () => {
	const zing = await launchZing()
	const a = await makePdf(join(zing.userDataDir, 'a.pdf'), [200, 300])
	const b = await makePdf(join(zing.userDataDir, 'b.pdf'), [400, 500, 600])
	const before = [readFileSync(a), readFileSync(b)]
	// The name the merge wants is taken: that file must stay as it is.
	mkdirSync(downloadsOf(zing), { recursive: true })
	writeFileSync(join(downloadsOf(zing), 'merged.pdf'), 'mine')

	const { panel } = await openPdf(zing, a, b)
	await expect(panel.getByText('2 files, 5 pages')).toBeVisible()
	await panel.getByRole('button', { name: 'Save merged PDF' }).click()

	await expect(panel.getByText('Saved to your Downloads folder:')).toBeVisible()
	await expect(panel.getByText('merged 2.pdf', { exact: true })).toBeVisible()
	expect(await widthsOf(join(downloadsOf(zing), 'merged 2.pdf'))).toEqual([200, 300, 400, 500, 600])
	expect(readFileSync(join(downloadsOf(zing), 'merged.pdf'), 'utf8')).toBe('mine')
	expect(readFileSync(a).equals(before[0]!)).toBe(true)
	expect(readFileSync(b).equals(before[1]!)).toBe(true)
	expect(filesIn(downloadsOf(zing))).toEqual(['merged 2.pdf', 'merged.pdf'])
	await zing.close()
})

test('pdf.js and pdf-lib load only when a PDF tool opens, and the page\'s content security policy is not broken', async () => {
	const zing = await launchZing()
	const a = await makePdf(join(zing.userDataDir, 'a.pdf'), [200, 300])
	await zing.chrome.getByRole('button', { name: 'Toolbox' }).first().click()
	const surface = await pageWhere(zing.browser, (p) => p.url().endsWith('#surface'))
	const complaints: string[] = []
	surface.on('console', (message) => message.type() === 'error' && complaints.push(message.text()))
	surface.on('pageerror', (error) => complaints.push(error.message))
	await surface.evaluate(() => {
		;(globalThis as unknown as { __csp: string[] }).__csp = []
		document.addEventListener('securitypolicyviolation', (event) => (globalThis as unknown as { __csp: string[] }).__csp.push(`${event.violatedDirective} ${event.blockedURI}`))
	})
	const loaded = (): Promise<string[]> => surface.evaluate(() => performance.getEntriesByType('resource').map((e) => e.name.split('/').pop()!))
	const heavy = /^(pdf|pdfEdit\.worker|pdf\.worker)/

	// The Toolbox and another tool are open: no PDF code has been fetched.
	await surface.getByTestId('toolbox-tile').filter({ hasText: 'Hashes' }).click()
	await expect(surface.getByRole('dialog', { name: 'Hashes', exact: true })).toBeVisible()
	await zing.chrome.getByRole('button', { name: 'Toolbox' }).first().click()
	await expect(surface.getByRole('dialog', { name: 'Toolbox' })).toBeVisible()
	expect((await loaded()).filter((name) => /^(Pdf|pdf)/.test(name))).toEqual([])
	expect(surface.workers()).toEqual([])

	// The PDF panel is open and empty: its own small file is fetched, the libraries are not.
	await surface.getByTestId('toolbox-tile').filter({ hasText: 'PDF Tools' }).click()
	const panel = surface.getByRole('dialog', { name: 'PDF Tools', exact: true })
	await expect(panel.getByText('Drop PDFs here')).toBeVisible()
	expect((await loaded()).some((name) => name.startsWith('PdfTool-'))).toBe(true)
	expect((await loaded()).filter((name) => heavy.test(name))).toEqual([])
	expect(surface.workers()).toEqual([])

	// A file is added: pdf-lib's worker and pdf.js with its worker start.
	await queuePick(zing, a)
	await panel.getByRole('button', { name: 'Choose PDFs…' }).click()
	await expect(panel.getByText('1 file, 2 pages')).toBeVisible()
	await expect.poll(async () => (await loaded()).filter((name) => heavy.test(name)).length).toBeGreaterThan(0)
	await expect.poll(() => surface.workers().length).toBeGreaterThanOrEqual(2)
	await expect.poll(() => darkPixels(surface, 0)).toBeGreaterThan(100)
	expect(await surface.evaluate(() => (globalThis as unknown as { __csp: string[] }).__csp)).toEqual([])
	expect(complaints.filter((text) => /content security|CSP|refused to/i.test(text))).toEqual([])
	await zing.close()
})

test('page thumbnails draw on a canvas, and with 500 pages only the ones on screen are drawn', async () => {
	const zing = await launchZing()
	const big = await makePdf(join(zing.userDataDir, 'big.pdf'), Array.from({ length: 500 }, () => 200))
	const { surface, panel } = await openPdf(zing, big)
	await expect(panel.getByText('1 file, 500 pages')).toBeVisible({ timeout: 60_000 })
	await expect.poll(() => darkPixels(surface, 0)).toBeGreaterThan(100)
	await expect(panel.getByRole('option')).toHaveCount(500)
	const drawn = await surface.evaluate(() => [...document.querySelectorAll<HTMLCanvasElement>('.pdf-thumb-canvas')].filter((c) => c.width > 0).length)
	expect(drawn).toBeGreaterThan(0)
	expect(drawn).toBeLessThan(60)

	// Scrolled to the end, the last page is drawn and the first has given its pixels back.
	await panel.getByRole('option').last().scrollIntoViewIfNeeded()
	await expect.poll(() => darkPixels(surface, 499)).toBeGreaterThan(100)
	await expect.poll(() => surface.evaluate(() => document.querySelectorAll<HTMLCanvasElement>('.pdf-thumb-canvas')[0]!.width)).toBe(0)
	await zing.close()
})

test('reorder by keyboard works, with visible focus and an announcement', async () => {
	const zing = await launchZing()
	const pdf = await makePdf(join(zing.userDataDir, 'three.pdf'), [200, 300, 400])
	const { surface, panel } = await openPdf(zing, pdf)
	await expect(panel.getByText('1 file, 3 pages')).toBeVisible()

	const first = panel.getByRole('option').first()
	await first.focus()
	await surface.keyboard.press('ArrowRight')
	await surface.keyboard.press('ArrowLeft')
	await surface.keyboard.press('Control+ArrowRight')
	await expect(panel.getByRole('status').filter({ hasText: 'Page moved to position 2 of 3.' })).toHaveCount(1)
	// Focus followed the page to its new place, and is drawn.
	const focus = await surface.evaluate(() => {
		const active = document.activeElement as HTMLElement
		return { label: active.getAttribute('aria-label'), outline: getComputedStyle(active).outlineStyle, width: getComputedStyle(active).outlineWidth }
	})
	expect(focus.label).toBe('Page 2')
	expect(focus.outline).not.toBe('none')
	expect(focus.width).not.toBe('0px')
	// Arrow keys move between pages; the end of the row is announced rather than silent.
	await surface.keyboard.press('ArrowRight')
	await surface.keyboard.press('Control+ArrowRight')
	await expect(panel.getByRole('status').filter({ hasText: 'Already the last page.' })).toHaveCount(1)
	await surface.keyboard.press('Control+ArrowLeft')
	await expect(panel.getByRole('status').filter({ hasText: 'Page moved to position 2 of 3.' })).toHaveCount(1)
	// Ctrl+Z takes the last move back, from the keyboard.
	await surface.keyboard.press('Control+z')
	await expect(panel.getByRole('status').filter({ hasText: 'Undone.' })).toHaveCount(1)

	await panel.getByRole('button', { name: 'Save PDF' }).click()
	await expect(panel.getByText('three-edited.pdf', { exact: true })).toBeVisible()
	const saved = await widthsOf(join(downloadsOf(zing), 'three-edited.pdf'))
	expect(saved).toEqual([300, 200, 400])
	await zing.close()
})

test('turning, deleting and undo change the saved file, never the original', async () => {
	const zing = await launchZing()
	const pdf = await makePdf(join(zing.userDataDir, 'doc.pdf'), [200, 300, 400])
	const original = readFileSync(pdf)
	const { panel } = await openPdf(zing, pdf)
	await expect(panel.getByText('1 file, 3 pages')).toBeVisible()

	await panel.getByRole('option').nth(1).click()
	await expect(panel.getByTestId('pdf-target')).toHaveText('Turn and Delete act on page 2.')
	await panel.getByRole('button', { name: 'Turn right' }).click()
	await panel.getByRole('button', { name: 'Delete', exact: true }).click()
	await expect(panel.getByText('1 file, 2 pages')).toBeVisible()
	await panel.getByRole('button', { name: 'Undo' }).click()
	await expect(panel.getByText('1 file, 3 pages')).toBeVisible()
	await panel.getByRole('button', { name: 'Redo' }).click()
	await expect(panel.getByText('1 file, 2 pages')).toBeVisible()
	await panel.getByRole('button', { name: 'Save PDF' }).click()
	await expect(panel.getByText('doc-edited.pdf', { exact: true })).toBeVisible()
	expect(await widthsOf(join(downloadsOf(zing), 'doc-edited.pdf'))).toEqual([200, 400])
	expect(readFileSync(pdf).equals(original)).toBe(true)
	await zing.close()
})

test('text, a picture and a typed signature are placed on a page; the signature is described as a picture', async () => {
	const zing = await launchZing()
	const pdf = await makePdf(join(zing.userDataDir, 'form.pdf'), [300])
	const original = readFileSync(pdf)
	const { panel } = await openPdf(zing, pdf)
	await expect(panel.getByText('1 file, 1 page')).toBeVisible()
	await panel.getByRole('option').first().click()
	await panel.getByRole('button', { name: 'Edit page…' }).click()

	await expect(panel.locator('.pdf-editor').getByText(/A signature here is a picture of your signature placed on the page. It is not a digital signature/)).toBeVisible()
	await panel.getByLabel('Text to add').fill('Hello Zing')
	await panel.getByRole('button', { name: 'Add text' }).click()
	await expect(panel.getByRole('button', { name: 'Text: Hello Zing' })).toBeVisible()
	await panel.getByRole('button', { name: 'Add signature…' }).click()
	await panel.getByLabel('Or type it').fill('Zed Zed')
	await panel.getByRole('button', { name: 'Place on page' }).click()
	await expect(panel.getByRole('button', { name: 'Picture or signature' })).toBeVisible()
	// Moved with the keyboard, which is the signature's only other way to move.
	await panel.getByRole('button', { name: 'Picture or signature' }).focus()
	await panel.page().keyboard.press('Shift+ArrowDown')
	await panel.getByRole('button', { name: 'Done' }).click()

	await panel.getByRole('button', { name: 'Save PDF' }).click()
	await expect(panel.getByText('form-edited.pdf', { exact: true })).toBeVisible()
	const out = await PDFDocument.load(readFileSync(join(downloadsOf(zing), 'form-edited.pdf')))
	expect(out.getPageCount()).toBe(1)
	const resources = out.getPage(0).node.Resources()!
	expect(resources.lookup(PDFName.of('XObject'))).toBeDefined()
	expect(resources.lookup(PDFName.of('Font'))).toBeDefined()
	expect(readFileSync(pdf).equals(original)).toBe(true)
	await zing.close()
})

test('a filled form keeps its fields, and merging says what it drops', async () => {
	const zing = await launchZing()
	const doc = await PDFDocument.create()
	const page = doc.addPage([300, 300])
	doc.getForm().createTextField('name').addToPage(page, { x: 10, y: 200, width: 150, height: 20 })
	const path = join(zing.userDataDir, 'application.pdf')
	writeFileSync(path, await doc.save())

	const { panel } = await openPdf(zing, path)
	await panel.getByLabel('name', { exact: true }).fill('Zed')
	await panel.getByRole('button', { name: 'Save PDF' }).click()
	await expect(panel.getByText('application-edited.pdf', { exact: true })).toBeVisible()
	const form = (await PDFDocument.load(readFileSync(join(downloadsOf(zing), 'application-edited.pdf')))).getForm()
	expect(form.getTextField('name').getText()).toBe('Zed')
	await panel.getByText("What this can and can't do").click()
	await expect(panel.getByText(/drops bookmarks and turns form fields into plain pictures/)).toBeVisible()
	await zing.close()
})

test('an encrypted PDF and a damaged one are refused by name, and nothing is written', async () => {
	const zing = await launchZing()
	const secret = join(zing.userDataDir, 'secret.pdf')
	copyFileSync(join(import.meta.dirname, '..', 'src', 'shared', 'pdf', 'fixtures', 'encrypted.pdf'), secret)
	const broken = join(zing.userDataDir, 'broken.pdf')
	writeFileSync(broken, '%PDF-1.4\nthis is not a pdf')
	const { panel } = await openPdf(zing, secret)
	await expect(panel.getByRole('alert')).toContainText('secret.pdf')
	await expect(panel.getByRole('alert')).toContainText('password')
	await expect(panel.getByText(/\d+ files?, \d+ pages?/)).toHaveCount(0)

	await queuePick(zing, broken)
	await panel.getByRole('button', { name: 'Choose PDFs…' }).click()
	await expect(panel.getByRole('alert')).toContainText('broken.pdf')
	expect(filesIn(downloadsOf(zing))).toEqual([])
	await zing.close()
})

test('a web page cannot read a PDF panel session or save through it', async () => {
	pageServer = createServer((_req, res) => {
		res.setHeader('content-type', 'text/html; charset=utf-8')
		res.end('<!DOCTYPE html><html><body>a page</body></html>')
	})
	await new Promise<void>((resolve) => pageServer!.listen(0, '127.0.0.1', resolve))
	const base = `http://127.0.0.1:${(pageServer.address() as AddressInfo).port}`
	const zing = await launchZing({ env: { ZING_START_URL: `${base}/x` } })
	const pdf = await makePdf(join(zing.userDataDir, 'a.pdf'), [200])
	const { panel } = await openPdf(zing, pdf)
	await expect(panel.getByText('1 file, 1 page')).toBeVisible()
	// A session for the open panel, as the interface itself asks for one.
	await queuePick(zing, pdf)
	const session = await zing.chrome.evaluate(async () => {
		const token = (await window.zing.pickFiles(['pdf']))[0]!.token
		const panelId = (await window.zing.getToolPanels()).panels[0]!.id
		return (await window.zing.openToolSession(panelId, token, ['pdf', 'image'])).url
	})
	expect(session).toMatch(/^https:\/\/view\.zing\/[0-9A-Za-z_-]+$/)
	const id = session.split('/').pop()!

	const page = await pageWhere(zing.browser, (p) => p.url() === `${base}/x`)
	const outcome = await page.evaluate(async ([read, save]) => {
		if ((globalThis as unknown as { chrome?: { webview?: unknown } }).chrome?.webview === undefined) throw new Error('chrome.webview is missing on this page, so the test proves nothing')
		const tried = async (url: string, init?: RequestInit): Promise<string> => {
			try {
				const response = await fetch(url, init)
				return `answered ${response.status}`
			} catch {
				return 'refused'
			}
		}
		return [await tried(read!), await tried(read!, { mode: 'no-cors' }), await tried(save!, { method: 'PUT', body: '%PDF-1.4 evil' })]
	}, [session, `https://view.zing/save/${id}/evil.pdf`])
	// A no-cors fetch gets an opaque answer (status 0, nothing readable) whatever the server said, as in bgremove.spec.
	expect(outcome).toEqual(['refused', 'answered 0', 'refused'])
	expect(filesIn(downloadsOf(zing))).toEqual([])
	await zing.close()
})

test('Convert turns a PDF into numbered PNG and JPEG pages, a picture into a PDF, and opens a PDF here', async () => {
	const zing = await launchZing()
	const pdf = await makePdf(join(zing.userDataDir, 'book.pdf'), [200, 300])
	const { surface, panel } = await openTool(zing, 'Convert')
	await queuePick(zing, pdf)
	await panel.getByRole('button', { name: /^Choose / }).click()
	await expect(panel.getByText('book.pdf', { exact: true })).toBeVisible()
	await panel.getByRole('button', { name: 'Convert to', exact: true }).click()
	// Pandoc can't read a PDF, so Word is not among the choices.
	await expect(panel.getByRole('option', { name: /Word/ })).toHaveCount(0)
	await panel.getByRole('option', { name: 'PNG' }).click()
	await panel.getByRole('button', { name: 'Convert', exact: true }).click()
	await expect(panel.getByText('Saved to your Downloads folder:')).toBeVisible({ timeout: 60_000 })
	expect(filesIn(downloadsOf(zing))).toEqual(['book-p001.png', 'book-p002.png'])
	for (const name of filesIn(downloadsOf(zing))) expect(readFileSync(join(downloadsOf(zing), name)).subarray(1, 4).toString('latin1')).toBe('PNG')

	// Again as JPEG: the names are numbered the same way, and the PNGs stay.
	await queuePick(zing, pdf)
	await panel.getByRole('button', { name: /^Choose / }).click()
	await panel.getByRole('button', { name: 'Convert to', exact: true }).click()
	await panel.getByRole('option', { name: 'JPG' }).click()
	await panel.getByRole('button', { name: 'Convert', exact: true }).click()
	await expect(panel.getByText('book-p002.jpg', { exact: true })).toBeVisible({ timeout: 60_000 })
	expect(filesIn(downloadsOf(zing))).toEqual(['book-p001.jpg', 'book-p001.png', 'book-p002.jpg', 'book-p002.png'])
	expect(readFileSync(join(downloadsOf(zing), 'book-p001.jpg')).subarray(0, 3).toString('hex')).toBe('ffd8ff')

	// A picture becomes a one-page PDF, which opens in PDF Tools with one click.
	await queuePick(zing, join(downloadsOf(zing), 'book-p001.png'))
	await panel.getByRole('button', { name: /^Choose / }).click()
	await panel.getByRole('button', { name: 'Convert to', exact: true }).click()
	await panel.getByRole('option', { name: 'PDF' }).click()
	await panel.getByRole('button', { name: 'Convert', exact: true }).click()
	await expect(panel.getByText('book-p001.pdf', { exact: true })).toBeVisible({ timeout: 60_000 })
	expect((await PDFDocument.load(readFileSync(join(downloadsOf(zing), 'book-p001.pdf')))).getPageCount()).toBe(1)
	await panel.getByRole('button', { name: 'Open in PDF Tools' }).click()
	const pdfPanel = surface.getByRole('dialog', { name: 'PDF Tools', exact: true })
	await expect(pdfPanel.getByText('1 file, 1 page')).toBeVisible()
	await zing.close()
})
