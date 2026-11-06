import { existsSync, readFileSync, writeFileSync } from 'node:fs'
import { join } from 'node:path'
import { expect, type Locator, type Page } from '@playwright/test'
import { PDFDocument, rgb } from 'pdf-lib'
import { drawFixture, encodePng } from '../src/shared/convert/bgFixture.node'
import type { ZingApi } from '../src/shared/ipc'
import { launchZing, pageWhere, test, type Launched } from './zing'

declare const window: { readonly zing: ZingApi; readonly screenX: number; readonly screenY: number }

// Files move between tools by token: a Convert output is dragged into the viewer, the PDF tools or the background remover. No
// engine runs (a PDF is drawn and a picture becomes a PDF in the page) and the real clipboard is never touched: in an e2e run
// the host keeps the CF_HDROP block it would have put there and the "read-clipboard" test message reads it back.

interface Panels {
	readonly surface: Page
	readonly convert: Locator
}

const downloadsOf = (zing: Launched): string => join(zing.userDataDir, 'downloads')

async function queuePick(zing: Launched, ...files: string[]): Promise<void> {
	await zing.chrome.evaluate((paths) => (globalThis as unknown as { chrome: { webview: { postMessage(m: unknown): void } } }).chrome.webview.postMessage({ kind: 'test', action: 'pick-files', files: paths }), files)
}

async function openPanel(zing: Launched, surface: Page | null, id: string, title: string, x: number): Promise<{ surface: Page; panel: Locator }> {
	await zing.chrome.evaluate(([panel, at]) => window.zing.openPanel(panel!, { x: window.screenX + Number(at), y: window.screenY + 120 }), [id, String(x)])
	const page = surface ?? (await pageWhere(zing.browser, (p) => p.url().endsWith('#surface')))
	const panel = page.getByRole('dialog', { name: title, exact: true })
	await expect(panel).toBeVisible()
	return { surface: page, panel }
}

const openConvert = async (zing: Launched): Promise<Panels> => {
	const { surface, panel } = await openPanel(zing, null, 'section:Convert', 'Convert', 300)
	return { surface, convert: panel }
}

async function convertTo(zing: Launched, convert: Locator, input: string, format: 'PNG' | 'PDF'): Promise<void> {
	await queuePick(zing, input)
	await convert.getByRole('button', { name: /^Choose / }).click()
	await convert.getByRole('button', { name: /^Convert to/ }).click()
	await convert.getByRole('option', { name: format }).click()
	await convert.getByRole('button', { name: 'Convert', exact: true }).click()
	await expect(convert.getByText('Saved to your Downloads folder:')).toBeVisible({ timeout: 60_000 })
}

async function makeBook(path: string): Promise<string> {
	const doc = await PDFDocument.create()
	for (const width of [200, 300]) doc.addPage([width, 200]).drawRectangle({ x: 10, y: 10, width: width - 20, height: 120, color: rgb(0, 0, 0) })
	writeFileSync(path, await doc.save())
	return path
}

function makePicture(path: string): string {
	writeFileSync(path, encodePng(drawFixture(64, 48).rgba, 64, 48))
	return path
}

/** Drags a made file from Convert's result to a drop zone of another tool. */
async function dragOutput(convert: Locator, name: string, zone: Locator): Promise<void> {
	await convert.locator('.file-row.draggable', { hasText: name }).dragTo(zone)
}

test('a PDF Convert made is dragged into the viewer and opens there', async () => {
	const zing = await launchZing()
	const { surface, convert } = await openConvert(zing)
	await convertTo(zing, convert, makePicture(join(zing.userDataDir, 'photo.png')), 'PDF')
	const viewer = (await openPanel(zing, surface, 'section:Files', 'Files', 920)).panel

	await dragOutput(convert, 'photo.pdf', viewer.locator('.float-body'))

	await expect(viewer.getByTestId('viewer-pdf')).toBeVisible()
	await zing.close()
})

test('a PDF Convert made is dragged into the PDF tool and opens there', async () => {
	const zing = await launchZing()
	const { surface, convert } = await openConvert(zing)
	await convertTo(zing, convert, makePicture(join(zing.userDataDir, 'photo.png')), 'PDF')
	const pdfTools = (await openPanel(zing, surface, 'tool:PDF Tools', 'PDF Tools', 920)).panel

	await dragOutput(convert, 'photo.pdf', pdfTools.locator('.convert-drop'))

	await expect(pdfTools.getByText('1 file, 1 page')).toBeVisible()
	await zing.close()
})

test('a picture Convert made is dragged into the background remover and loads there', async () => {
	const zing = await launchZing()
	const { surface, convert } = await openConvert(zing)
	await convertTo(zing, convert, await makeBook(join(zing.userDataDir, 'book.pdf')), 'PNG')
	const remover = (await openPanel(zing, surface, 'tool:Background Remover', 'Background Remover', 920)).panel
	await expect(remover.getByText('Drop a picture here')).toBeVisible()

	await dragOutput(convert, 'book-p001.png', remover.locator('.convert-drop'))

	await expect(remover.locator('.convert-drop').getByText('book-p001.png', { exact: true })).toBeVisible()
	await expect(remover.getByRole('button', { name: 'Remove background' })).toBeVisible()
	await zing.close()
})

test('text and a made-up tool file are refused by a file tool, and a real file drag is accepted', async () => {
	const zing = await launchZing()
	const { surface } = await openConvert(zing)
	const remover = (await openPanel(zing, surface, 'tool:Background Remover', 'Background Remover', 920)).panel
	const zone = remover.locator('.convert-drop')

	const offered = (type: string, value: string): Promise<{ over: boolean; drop: boolean }> =>
		zone.evaluate(
			(element, [dragType, data]) => {
				const transfer = new DataTransfer()
				const isFile = dragType === 'Files'
				if (isFile) transfer.items.add(new File(['x'], 'x.png', { type: 'image/png' }))
				else transfer.setData(dragType!, data!)
				const send = (kind: string): boolean => {
					const event = new DragEvent(kind, { bubbles: true, cancelable: true, dataTransfer: transfer })
					element.dispatchEvent(event)
					return event.defaultPrevented
				}
				// A real file's drop is not sent: it would ask the host for a drop that never happened.
				return { over: send('dragover'), drop: isFile ? false : send('drop') }
			},
			[type, value],
		)

	// The control: a file drag is taken, so the refusals below are not just a test that can't see anything.
	expect((await offered('Files', '')).over).toBe(true)
	expect(await offered('text/plain', 'just some words')).toEqual({ over: false, drop: false })
	expect(await offered('text/uri-list', 'https://example.com/a.png')).toEqual({ over: false, drop: false })
	expect(await offered('application/x-zing-tool-file', 'f'.repeat(32))).toEqual({ over: false, drop: false })
	await expect(remover.getByText('Drop a picture here')).toBeVisible()
	await zing.close()
})

test('Copy file puts the file on the clipboard as CF_HDROP', async () => {
	const zing = await launchZing()
	const { convert } = await openConvert(zing)
	await convertTo(zing, convert, makePicture(join(zing.userDataDir, 'photo.png')), 'PDF')

	const copy = convert.getByRole('button', { name: 'Copy photo.pdf to the clipboard' })
	await copy.click()
	await expect(copy).toHaveText('Copied')

	const report = join(zing.userDataDir, 'clipboard.json')
	await zing.chrome.evaluate((file) => (globalThis as unknown as { chrome: { webview: { postMessage(m: unknown): void } } }).chrome.webview.postMessage({ kind: 'test', action: 'read-clipboard', file }), report)
	await expect.poll(() => existsSync(report)).toBe(true)
	const held = JSON.parse(readFileSync(report, 'utf8')) as { format: string; paths: string[] }
	expect(held.format).toBe('CF_HDROP')
	expect(held.paths.map((p) => p.toLowerCase())).toEqual([join(downloadsOf(zing), 'photo.pdf').toLowerCase()])
	await zing.close()
})
