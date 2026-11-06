import { PDFArray, PDFDict, PDFDocument, PDFName, PDFRef, PDFStream, PDFString, type PDFObject, type PDFPage } from 'pdf-lib'
import { describe, expect, it } from 'vitest'
import { deletePages, fillForm, listFormFields, mergePdfs, splitPdf } from './pdfEdit'

const SECRET = 'SECRETPAGE3'

const pageObjects = (doc: PDFDocument): number =>
	doc.context.enumerateIndirectObjects().filter(([, o]) => (o as { get?: (n: PDFName) => unknown }).get?.(PDFName.of('Type')) === PDFName.of('Page')).length

const FITS = ['XYZ', 'Fit', 'FitH', 'FitV', 'FitR', 'FitB', 'FitBH', 'FitBV'].map((fit) => PDFName.of(fit))

/**
 * Destinations left with no page, such as [/Fit], wherever they are: under /D or /Dest, in a /Dests dictionary, in a name tree
 * or as their own object. It is what is left when only the page reference of a cut destination is taken out.
 */
function deadDestinations(doc: PDFDocument): number {
	let dead = 0
	const look = (object: PDFObject): void => {
		if (object instanceof PDFStream) look(object.dict)
		else if (object instanceof PDFDict) for (const [, value] of object.entries()) look(value)
		else if (object instanceof PDFArray) {
			if (FITS.includes(object.get(0) as PDFName)) dead++
			object.asArray().forEach(look)
		}
	}
	for (const [, object] of doc.context.enumerateIndirectObjects()) look(object)
	return dead
}

async function expectNoLeak(bytes: Uint8Array, kept: number): Promise<void> {
	const loaded = await PDFDocument.load(bytes)
	expect(deadDestinations(loaded)).toBe(0)
	expect(pageObjects(loaded)).toBe(kept)
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

/**
 * A document's named destinations, as LaTeX writes them: an old-style /Dests dictionary, a /Dests name tree with three leaves
 * (values that are actions by reference, direct arrays and arrays by reference) and an outline-like item whose /Dest is its own
 * object. A rebuild does not copy the catalog, so they are hung on page 1, which is how they reach the copy.
 */
function namedDestinations(doc: PDFDocument, pages: PDFPage[]): void {
	const { context } = doc
	const [kept, lost] = [pages[1]!.ref, pages[2]!.ref]
	const action = (page: PDFPage): PDFRef => context.register(context.obj({ S: 'GoTo', D: [page.ref, 'Fit'] }))
	const own = (page: PDFPage): PDFRef => context.register(context.obj([page.ref, 'Fit']))
	const leaf = (names: PDFArray, low: string, high: string): PDFRef => context.register(context.obj({ Names: names, Limits: [PDFString.of(low), PDFString.of(high)] }))
	const tree = context.obj({
		Kids: [
			leaf(context.obj([PDFString.of('a'), action(pages[2]!), PDFString.of('b'), action(pages[1]!), PDFString.of('c'), [lost, 'Fit']]), 'a', 'c'),
			leaf(context.obj([PDFString.of('d'), own(pages[2]!), PDFString.of('e'), [kept, 'Fit']]), 'd', 'e'),
			leaf(context.obj([PDFString.of('f'), [lost, 'FitH', 0]]), 'f', 'f'),
		],
	})
	const dests = context.obj({ gone: [lost, 'Fit'], goneByRef: own(pages[2]!), stays: [kept, 'Fit'] })
	const item = context.obj({ Title: PDFString.of('Section 3'), Dest: own(pages[2]!) })
	pages[0]!.node.set(PDFName.of('Doc'), context.obj({ Dests: dests, Names: { Dests: tree }, Item: item }))
}

/** The name tree's names and values, leaf by leaf, with each leaf's /Limits. */
function readTree(tree: PDFDict): { names: Record<string, PDFObject>; limits: string[][] } {
	const names: Record<string, PDFObject> = {}
	const limits: string[][] = []
	for (const kid of tree.lookup(PDFName.of('Kids'), PDFArray).asArray()) {
		const leaf = tree.context.lookup(kid, PDFDict)
		const pairs = leaf.lookup(PDFName.of('Names'), PDFArray)
		for (let at = 0; at < pairs.size(); at += 2) names[(pairs.get(at) as PDFString).decodeText()] = pairs.get(at + 1)
		limits.push(leaf.lookup(PDFName.of('Limits'), PDFArray).asArray().map((s) => (s as PDFString).decodeText()))
	}
	return { names, limits }
}

describe('named destinations to a left-out page', () => {
	const runs: [string, (bytes: Uint8Array) => Promise<Uint8Array>][] = [
		['delete', (bytes) => deletePages(bytes, [3])],
		['split', async (bytes) => (await splitPdf(bytes, [{ from: 1, to: 2 }]))[0]!],
	]
	for (const [name, run] of runs) {
		it(`${name}: are gone with their names, and the ones to kept pages still lead there`, async () => {
			const bytes = await run(await threePages(namedDestinations))
			await expectNoLeak(bytes, 2)
			const loaded = await PDFDocument.load(bytes)
			const page2 = loaded.getPages()[1]!.ref
			const target = (dest: PDFObject): PDFObject => (loaded.context.lookup(dest) as PDFArray).get(0)
			const doc = loaded.getPages()[0]!.node.lookup(PDFName.of('Doc'), PDFDict)

			const { names, limits } = readTree(doc.lookup(PDFName.of('Names'), PDFDict).lookup(PDFName.of('Dests'), PDFDict))
			expect(Object.keys(names)).toEqual(['b', 'e'])
			expect(target(loaded.context.lookup(names.b!, PDFDict).get(PDFName.of('D')))).toBe(page2)
			expect(target(names.e!)).toBe(page2)
			expect(limits).toEqual([['b', 'b'], ['e', 'e']])

			const dests = doc.lookup(PDFName.of('Dests'), PDFDict)
			expect(dests.keys().map((key) => key.decodeText())).toEqual(['stays'])
			expect(target(dests.get(PDFName.of('stays'))!)).toBe(page2)

			const item = doc.lookup(PDFName.of('Item'), PDFDict)
			expect(item.has(PDFName.of('Dest'))).toBe(false)
		})
	}
})

describe('which pages a field is on', () => {
	it('is listed with each field, counted from 1', async () => {
		const doc = await PDFDocument.create()
		const pages = [doc.addPage([100, 100]), doc.addPage([100, 100])]
		doc.getForm().createTextField('first').addToPage(pages[0]!, { x: 5, y: 50, width: 80, height: 15 })
		const radio = doc.getForm().createRadioGroup('both')
		radio.addOptionToPage('a', pages[0]!, { x: 5, y: 5, width: 10, height: 10 })
		radio.addOptionToPage('b', pages[1]!, { x: 5, y: 5, width: 10, height: 10 })
		const fields = await listFormFields(await doc.save())
		expect(fields.map((f) => [f.name, f.pages])).toEqual([['first', [1]], ['both', [1, 2]]])
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
