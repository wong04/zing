import { readFileSync } from 'node:fs'
import { PDFDocument, PDFName, PDFString, degrees } from 'pdf-lib'
import { describe, expect, it } from 'vitest'
import {
	PdfEditError,
	PDF_LIMITS,
	addOverlay,
	buildPdf,
	deletePages,
	fillForm,
	listFormFields,
	mergePdfs,
	parsePageRanges,
	reorderPages,
	rotatePages,
	splitPdf,
	visualToUser,
} from './pdfEdit'

/** A PDF of `count` pages; page n is `n * 100` points wide, so a page can be told apart by its size. */
async function fixture(count: number, startWidth = 100): Promise<Uint8Array> {
	const doc = await PDFDocument.create()
	for (let i = 0; i < count; i++) doc.addPage([startWidth + i * 100, 200])
	return doc.save()
}

const widthsOf = async (bytes: Uint8Array): Promise<number[]> => (await PDFDocument.load(bytes)).getPages().map((p) => p.getWidth())

async function formFixture(): Promise<Uint8Array> {
	const doc = await PDFDocument.create()
	const page = doc.addPage([300, 300])
	const form = doc.getForm()
	const name = form.createTextField('name')
	name.addToPage(page, { x: 10, y: 200, width: 150, height: 20 })
	const agree = form.createCheckBox('agree')
	agree.addToPage(page, { x: 10, y: 150, width: 15, height: 15 })
	const colour = form.createDropdown('colour')
	colour.addOptions(['red', 'green'])
	colour.addToPage(page, { x: 10, y: 100, width: 100, height: 20 })
	// A bookmark pointing at the page.
	const { context } = doc
	const outline = context.obj({ Type: 'Outlines', Count: 1 })
	const item = context.obj({ Title: PDFString.of('Start'), Parent: context.register(outline), Dest: [page.ref, 'Fit'] })
	const itemRef = context.register(item)
	outline.set(PDFName.of('First'), itemRef)
	outline.set(PDFName.of('Last'), itemRef)
	doc.catalog.set(PDFName.of('Outlines'), context.register(outline))
	return doc.save()
}

describe('PdfEditError', () => {
	it('is an Error with its own name', () => {
		const error = new PdfEditError('x')
		expect(error).toBeInstanceOf(Error)
		expect(error.name).toBe('PdfEditError')
	})
})

describe('parsePageRanges', () => {
	it('reads ranges and single pages, in the order given', () => {
		expect(parsePageRanges('1-2, 5', 6)).toEqual([
			{ from: 1, to: 2 },
			{ from: 5, to: 5 },
		])
	})
	it('refuses a page past the end', () => {
		expect(() => parsePageRanges('9', 6)).toThrow(PdfEditError)
		expect(() => parsePageRanges('4-9', 6)).toThrow(/6 pages/)
	})
	it('refuses page 0, a backwards range, an empty list and words', () => {
		for (const bad of ['0', '3-1', '', '  ', '1,,2', 'a', '1-', '-2', '1.5']) expect(() => parsePageRanges(bad, 6), bad).toThrow(PdfEditError)
	})
	it('refuses more ranges than a split may make', () => {
		const many = Array.from({ length: PDF_LIMITS.maxSplitParts + 1 }, () => '1').join(',')
		expect(() => parsePageRanges(many, 6)).toThrow(/at most/)
	})
})

describe('merge, split, reorder, rotate, delete', () => {
	it('merges a 2-page and a 3-page file into 5 pages in order', async () => {
		const merged = await mergePdfs([await fixture(2, 100), await fixture(3, 1000)])
		expect(await widthsOf(merged)).toEqual([100, 200, 1000, 1100, 1200])
	})
	it('refuses a merge of fewer than two files, and of too many pages', async () => {
		await expect(mergePdfs([await fixture(1)])).rejects.toThrow(PdfEditError)
		const big = await fixture(PDF_LIMITS.maxPages)
		await expect(mergePdfs([big, await fixture(1)])).rejects.toThrow(/pages/)
	})
	it('splits into one file per range', async () => {
		const parts = await splitPdf(await fixture(6), parsePageRanges('1-2, 5', 6))
		expect(await Promise.all(parts.map(widthsOf))).toEqual([[100, 200], [500]])
	})
	it('reorders by a permutation of 1-based page numbers', async () => {
		expect(await widthsOf(await reorderPages(await fixture(3), [3, 1, 2]))).toEqual([300, 100, 200])
	})
	it('refuses an order that is not a permutation', async () => {
		const file = await fixture(3)
		for (const bad of [[1, 2], [1, 1, 2], [1, 2, 4], [0, 1, 2]]) await expect(reorderPages(file, bad), String(bad)).rejects.toThrow(PdfEditError)
	})
	it('deletes pages, and refuses to delete them all', async () => {
		const file = await fixture(3)
		expect(await widthsOf(await deletePages(file, [2]))).toEqual([100, 300])
		await expect(deletePages(file, [1, 2, 3])).rejects.toThrow(/every page/)
		await expect(deletePages(file, [4])).rejects.toThrow(PdfEditError)
	})
	it('adds to a page\'s existing rotation', async () => {
		const doc = await PDFDocument.create()
		doc.addPage([100, 100]).setRotation(degrees(90))
		doc.addPage([100, 100])
		const once = await rotatePages(await doc.save(), [1, 2], 90)
		const pages = (await PDFDocument.load(once)).getPages()
		expect(pages.map((p) => p.getRotation().angle)).toEqual([180, 90])
		const wrapped = await rotatePages(once, [1], 270)
		expect((await PDFDocument.load(wrapped)).getPage(0).getRotation().angle).toBe(90)
	})
	it('never changes the bytes it was given', async () => {
		const file = await fixture(3)
		const before = file.slice()
		await deletePages(file, [1])
		await rotatePages(file, [1], 90)
		await reorderPages(file, [3, 2, 1])
		expect(file).toEqual(before)
	})
})

describe('overlays', () => {
	const png = Uint8Array.from(Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==', 'base64'))

	it('leaves the page count alone and adds content to the page', async () => {
		const file = await fixture(2)
		expect((await PDFDocument.load(file)).getPage(0).node.Contents()).toBeUndefined()
		const out = await addOverlay(file, { page: 1, items: [{ kind: 'text', text: 'Hello', x: 20, y: 30, size: 12 }] })
		const after = await PDFDocument.load(out)
		expect(after.getPageCount()).toBe(2)
		const contents = after.getPage(0).node.Contents()
		expect(contents).toBeDefined()
		expect(out.length).toBeGreaterThan(file.length)
		// The second page is untouched.
		expect(after.getPage(1).node.Contents()).toBeUndefined()
	})
	it('places an image and keeps the page count', async () => {
		const out = await addOverlay(await fixture(1), { page: 1, items: [{ kind: 'image', bytes: png, format: 'png', x: 10, y: 10, width: 50, height: 50 }] })
		const page = (await PDFDocument.load(out)).getPage(0)
		expect(page.node.Resources()?.lookup(PDFName.of('XObject'))).toBeDefined()
	})
	it('refuses a character Helvetica cannot draw, naming it', async () => {
		await expect(addOverlay(await fixture(1), { page: 1, items: [{ kind: 'text', text: 'ok 中', x: 0, y: 0, size: 12 }] })).rejects.toThrow(/中/)
	})
	it('refuses a page that is not there and bytes that are not an image', async () => {
		await expect(addOverlay(await fixture(1), { page: 2, items: [] })).rejects.toThrow(PdfEditError)
		await expect(addOverlay(await fixture(1), { page: 1, items: [{ kind: 'image', bytes: new Uint8Array([1, 2, 3]), format: 'png', x: 0, y: 0, width: 5, height: 5 }] })).rejects.toThrow(PdfEditError)
	})
	it('maps a point on the page as shown to the page\'s own space, for every rotation', () => {
		// A 200 wide, 100 high page. Shown rotated, its width and height swap.
		const page = { x: 0, y: 0, width: 200, height: 100 }
		expect(visualToUser(page, 0, 10, 20)).toEqual({ x: 10, y: 80 })
		expect(visualToUser(page, 90, 0, 0)).toEqual({ x: 0, y: 0 })
		expect(visualToUser(page, 90, 100, 0)).toEqual({ x: 0, y: 100 })
		expect(visualToUser(page, 180, 0, 0)).toEqual({ x: 200, y: 0 })
		expect(visualToUser(page, 270, 0, 0)).toEqual({ x: 200, y: 100 })
		expect(visualToUser({ x: 5, y: 7, width: 200, height: 100 }, 0, 0, 0)).toEqual({ x: 5, y: 107 })
	})
	it('draws on a rotated page without error and keeps its rotation', async () => {
		const doc = await PDFDocument.create()
		doc.addPage([200, 100]).setRotation(degrees(90))
		const out = await addOverlay(await doc.save(), { page: 1, items: [{ kind: 'text', text: 'x', x: 5, y: 5, size: 10 }] })
		expect((await PDFDocument.load(out)).getPage(0).getRotation().angle).toBe(90)
	})
})

describe('forms', () => {
	it('lists the fields with their kind', async () => {
		const fields = await listFormFields(await formFixture())
		expect(fields.map((f) => [f.name, f.kind])).toEqual([
			['name', 'text'],
			['agree', 'checkbox'],
			['colour', 'dropdown'],
		])
		expect(fields[2]!.options).toEqual(['red', 'green'])
	})
	it('fills fields and reads them back, leaving them editable (not flattened)', async () => {
		const out = await fillForm(await formFixture(), { name: 'Zed', agree: 'true', colour: 'green' })
		const form = (await PDFDocument.load(out)).getForm()
		expect(form.getTextField('name').getText()).toBe('Zed')
		expect(form.getCheckBox('agree').isChecked()).toBe(true)
		expect(form.getDropdown('colour').getSelected()).toEqual(['green'])
		expect(form.getFields()).toHaveLength(3)
	})
	it('refuses a field that is not there, and an option the dropdown lacks', async () => {
		await expect(fillForm(await formFixture(), { nope: 'x' })).rejects.toThrow(/nope/)
		await expect(fillForm(await formFixture(), { colour: 'blue' })).rejects.toThrow(PdfEditError)
	})
	it('refuses text Helvetica cannot draw instead of writing a blank field', async () => {
		await expect(fillForm(await formFixture(), { name: '中' })).rejects.toThrow(PdfEditError)
	})
})

describe('what merging keeps', () => {
	it('keeps bookmarks and fillable fields when one file is edited, and drops both when files are merged', async () => {
		const form = await formFixture()
		const single = await PDFDocument.load(await rotatePages(form, [1], 90))
		expect(single.catalog.has(PDFName.of('Outlines'))).toBe(true)
		expect(single.getForm().getFields()).toHaveLength(3)

		const merged = await PDFDocument.load(await mergePdfs([form, form]))
		expect(merged.catalog.has(PDFName.of('Outlines'))).toBe(false)
		expect(merged.getForm().getFields()).toHaveLength(0)
	})
})

describe('buildPdf', () => {
	it('assembles pages from several files in the planned order with rotation and overlays', async () => {
		const out = await buildPdf(
			[
				{ name: 'a.pdf', bytes: await fixture(2, 100) },
				{ name: 'b.pdf', bytes: await fixture(2, 1000) },
			],
			{
				pages: [
					{ source: 1, page: 2, rotate: 90 },
					{ source: 0, page: 1, rotate: 0 },
				],
				overlays: [{ page: 2, items: [{ kind: 'text', text: 'hi', x: 1, y: 1, size: 9 }] }],
			},
		)
		const doc = await PDFDocument.load(out)
		expect(doc.getPages().map((p) => [p.getWidth(), p.getRotation().angle])).toEqual([[1100, 90], [100, 0]])
		expect(doc.getPage(1).node.Contents()).toBeDefined()
	})
	it('works on the loaded file itself for one file, so its fields survive a reorder', async () => {
		const doc = await PDFDocument.create()
		doc.addPage([100, 100])
		const page2 = doc.addPage([200, 100])
		doc.getForm().createTextField('t').addToPage(page2, { x: 1, y: 1, width: 50, height: 10 })
		const out = await buildPdf([{ name: 'f.pdf', bytes: await doc.save() }], { pages: [{ source: 0, page: 2, rotate: 0 }, { source: 0, page: 1, rotate: 0 }], overlays: [], formValues: { t: 'v' } })
		const loaded = await PDFDocument.load(out)
		expect(loaded.getPages().map((p) => p.getWidth())).toEqual([200, 100])
		expect(loaded.getForm().getTextField('t').getText()).toBe('v')
	})
	it('refuses a plan that names a page or a file that is not there, and an empty plan', async () => {
		const sources = [{ name: 'a.pdf', bytes: await fixture(1) }]
		await expect(buildPdf(sources, { pages: [{ source: 0, page: 2, rotate: 0 }], overlays: [] })).rejects.toThrow(PdfEditError)
		await expect(buildPdf(sources, { pages: [{ source: 1, page: 1, rotate: 0 }], overlays: [] })).rejects.toThrow(PdfEditError)
		await expect(buildPdf(sources, { pages: [], overlays: [] })).rejects.toThrow(PdfEditError)
	})
})

describe('files Zing will not edit', () => {
	const encrypted = new Uint8Array(readFileSync(new URL('./fixtures/encrypted.pdf', import.meta.url)))

	it('refuses an encrypted PDF, naming the file, and never writes it', async () => {
		await expect(rotatePages({ name: 'secret.pdf', bytes: encrypted }, [1], 90)).rejects.toThrow(/secret\.pdf.*password/s)
		await expect(mergePdfs([{ name: 'secret.pdf', bytes: encrypted }, { name: 'ok.pdf', bytes: await fixture(1) }])).rejects.toThrow(/secret\.pdf/)
	})
	it('refuses a corrupt file, naming it', async () => {
		const bytes = new TextEncoder().encode('%PDF-1.4\nthis is not a pdf')
		await expect(deletePages({ name: 'broken.pdf', bytes }, [1])).rejects.toThrow(/broken\.pdf/)
		await expect(splitPdf({ name: 'empty.pdf', bytes: new Uint8Array() }, [{ from: 1, to: 1 }])).rejects.toThrow(/empty\.pdf/)
	})
	it('refuses a file with more pages than the limit', async () => {
		const doc = await PDFDocument.create()
		for (let i = 0; i < PDF_LIMITS.maxPages + 1; i++) doc.addPage([10, 10])
		await expect(rotatePages({ name: 'huge.pdf', bytes: await doc.save() }, [1], 90)).rejects.toThrow(/huge\.pdf.*pages/s)
	})
})

