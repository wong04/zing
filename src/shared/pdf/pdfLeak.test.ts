import { PDFDocument, PDFName, PDFString, type PDFPage } from 'pdf-lib'
import { describe, expect, it } from 'vitest'
import { deletePages, fillForm, mergePdfs, splitPdf } from './pdfEdit'

const SECRET = 'SECRETPAGE3'

const pageObjects = (doc: PDFDocument): number =>
	doc.context.enumerateIndirectObjects().filter(([, o]) => (o as { get?: (n: PDFName) => unknown }).get?.(PDFName.of('Type')) === PDFName.of('Page')).length

async function expectNoLeak(bytes: Uint8Array, kept: number): Promise<void> {
	expect(pageObjects(await PDFDocument.load(bytes))).toBe(kept)
	expect(Buffer.from(bytes).toString('latin1')).not.toContain(SECRET)
}

/** Three pages; page 3 says SECRETPAGE3. `wire` connects pages 1 and 2 to page 3 in some way. */
async function threePages(wire: (doc: PDFDocument, pages: PDFPage[]) => void): Promise<Uint8Array> {
	const doc = await PDFDocument.create()
	const pages = [doc.addPage([100, 100]), doc.addPage([100, 100]), doc.addPage([100, 100])]
	pages[2]!.drawText(SECRET, { x: 5, y: 50, size: 10 })
	wire(doc, pages)
	return doc.save({ useObjectStreams: false })
}

describe('a page that is left out is not in the file, whatever pointed at it', () => {
	const cases: [string, (doc: PDFDocument, pages: PDFPage[]) => void][] = [
		[
			'a radio group with widgets on a kept and a dropped page',
			(doc, pages) => {
				const radio = doc.getForm().createRadioGroup('choice')
				radio.addOptionToPage('a', pages[0]!, { x: 5, y: 5, width: 10, height: 10 })
				radio.addOptionToPage('b', pages[2]!, { x: 5, y: 5, width: 10, height: 10 })
				radio.select('b')
			},
		],
		[
			'an /AA action on a page',
			(doc, pages) => {
				const { context } = doc
				pages[0]!.node.set(PDFName.of('AA'), context.obj({ O: { S: 'GoTo', D: [pages[2]!.ref, 'Fit'] } }))
			},
		],
		[
			'an /AA action on an annotation',
			(doc, pages) => {
				const { context } = doc
				const annot = context.register(context.obj({ Type: 'Annot', Subtype: 'Widget', Rect: [0, 0, 5, 5], AA: { E: { S: 'GoTo', D: [pages[2]!.ref, 'Fit'] } } }))
				pages[0]!.node.set(PDFName.of('Annots'), context.obj([annot]))
			},
		],
		[
			'an article bead',
			(doc, pages) => {
				const { context } = doc
				const first = context.nextRef()
				const second = context.nextRef()
				context.assign(first, context.obj({ Type: 'Bead', P: pages[0]!.ref, R: [0, 0, 5, 5], N: second, V: second }))
				context.assign(second, context.obj({ Type: 'Bead', P: pages[2]!.ref, R: [0, 0, 5, 5], N: first, V: first }))
				pages[0]!.node.set(PDFName.of('B'), context.obj([first]))
			},
		],
		[
			'a chain of actions where /Next is an array',
			(doc, pages) => {
				const { context } = doc
				const link = context.register(context.obj({ Type: 'Annot', Subtype: 'Link', Rect: [0, 0, 5, 5], A: { S: 'URI', URI: PDFString.of('https://example.com'), Next: [{ S: 'GoTo', D: [pages[2]!.ref, 'Fit'] }] } }))
				pages[0]!.node.set(PDFName.of('Annots'), context.obj([link]))
			},
		],
	]

	for (const [name, wire] of cases) {
		it(`${name}: delete`, async () => expectNoLeak(await deletePages(await threePages(wire), [3]), 2))
		it(`${name}: split`, async () => expectNoLeak((await splitPdf(await threePages(wire), [{ from: 1, to: 2 }]))[0]!, 2))
	}

	it('merge keeps every page and still has no extra page object', async () => {
		const merged = await mergePdfs([await threePages(cases[0]![1]), await threePages(cases[3]![1])])
		expect(pageObjects(await PDFDocument.load(merged))).toBe(6)
	})
})

describe('filling a form after pages were deleted', () => {
	it('keeps the form for fields whose widgets all survive, and fills it', async () => {
		const doc = await PDFDocument.create()
		const pages = [doc.addPage([100, 100]), doc.addPage([100, 100])]
		doc.getForm().createTextField('name').addToPage(pages[0]!, { x: 5, y: 50, width: 80, height: 15 })
		doc.getForm().createTextField('gone').addToPage(pages[1]!, { x: 5, y: 50, width: 80, height: 15 })
		const smaller = await deletePages(await doc.save(), [2])
		const kept = (await PDFDocument.load(smaller)).getForm().getFields().map((f) => f.getName())
		expect(kept).toEqual(['name'])
		const filled = await fillForm(smaller, { name: 'Zed' })
		expect((await PDFDocument.load(filled)).getForm().getTextField('name').getText()).toBe('Zed')
	})
	it('drops a field with a widget on a deleted page, with its value, and says no such field when asked to fill it', async () => {
		const doc = await PDFDocument.create()
		const pages = [doc.addPage([100, 100]), doc.addPage([100, 100])]
		const field = doc.getForm().createTextField('gone')
		field.setText(SECRET)
		field.addToPage(pages[1]!, { x: 5, y: 50, width: 80, height: 15 })
		const smaller = await deletePages(await doc.save({ useObjectStreams: false }), [2])
		expect(Buffer.from(smaller).toString('latin1')).not.toContain(SECRET)
		await expect(fillForm(smaller, { gone: 'x' })).rejects.toThrow(/gone/)
	})
})
