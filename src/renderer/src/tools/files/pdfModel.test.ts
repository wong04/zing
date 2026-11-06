import { describe, expect, it } from 'vitest'
import { PDF_LIMITS, PdfEditError } from '@shared/pdf/pdfCore'
import {
	addFile,
	amend,
	checkSessionBytes,
	addItem,
	deletePages,
	emptyModel,
	fieldsOnKeptPages,
	onlyFields,
	moveItem,
	movePage,
	redo,
	removeItem,
	rotatePages,
	setFormValue,
	startHistory,
	toPlan,
	toPlanRange,
	trim,
	undo,
	commit,
	type History,
	type Model,
} from './pdfModel'

const text = { kind: 'text', text: 'hi', x: 1, y: 2, size: 12 } as const

function twoFiles(): Model {
	return addFile(addFile(emptyModel(), { source: 0, name: 'a.pdf', pageCount: 2 }), { source: 1, name: 'b.pdf', pageCount: 3 })
}

describe('the page list', () => {
	it('lists the pages of each file in turn', () => {
		const model = twoFiles()
		expect(model.pages.map((p) => [p.source, p.page])).toEqual([[0, 1], [0, 2], [1, 1], [1, 2], [1, 3]])
		expect(new Set(model.pages.map((p) => p.key)).size).toBe(5)
	})
	it('moves a page one place, and stops at the ends', () => {
		const model = twoFiles()
		const second = model.pages[1]!.key
		expect(movePage(model, second, -1).pages[0]!.key).toBe(second)
		expect(movePage(model, model.pages[0]!.key, -1)).toBe(model)
		expect(movePage(model, model.pages[4]!.key, 1)).toBe(model)
	})
	it('turns pages further each time', () => {
		let model = twoFiles()
		const key = model.pages[0]!.key
		model = rotatePages(model, [key], 90)
		model = rotatePages(model, [key], 270)
		model = rotatePages(model, [key], 180)
		expect(model.pages[0]!.rotate).toBe(180)
	})
	it('deletes pages and their items, but never every page', () => {
		let model = twoFiles()
		const first = model.pages[0]!.key
		model = addItem(model, first, text)
		const gone = deletePages(model, [first])
		expect(gone.pages).toHaveLength(4)
		expect(gone.items).toHaveLength(0)
		expect(() => deletePages(model, model.pages.map((p) => p.key))).toThrow(PdfEditError)
	})
	it('refuses a session over the memory limit', () => {
		expect(() => checkSessionBytes(0, PDF_LIMITS.maxSessionBytes + 1, 'big.pdf')).toThrow(/memory/)
		expect(() => checkSessionBytes(PDF_LIMITS.maxSessionBytes, 1, 'b.pdf')).toThrow(PdfEditError)
		expect(() => checkSessionBytes(PDF_LIMITS.maxSessionBytes - 1, 1, 'b.pdf')).not.toThrow()
	})
	it('refuses a session over the page limit', () => {
		const model = addFile(emptyModel(), { source: 0, name: 'a.pdf', pageCount: PDF_LIMITS.maxPages })
		expect(() => addFile(model, { source: 1, name: 'b.pdf', pageCount: 1 })).toThrow(/pages/)
	})
})

describe('items placed on a page', () => {
	it('adds, moves and removes an item', () => {
		let model = twoFiles()
		const page = model.pages[0]!.key
		model = addItem(model, page, text)
		const id = model.items[0]!.id
		model = moveItem(model, id, { x: 50, y: 60 })
		expect(model.items[0]!.item).toMatchObject({ x: 50, y: 60, text: 'hi' })
		expect(removeItem(model, id).items).toHaveLength(0)
	})
})

describe('the plan', () => {
	it('names pages in order with their turn, and puts each item on its page\'s place in the result', () => {
		let model = twoFiles()
		const [a1, a2] = model.pages
		model = addItem(model, a2!.key, text)
		model = movePage(model, a2!.key, -1)
		model = rotatePages(model, [a1!.key], 90)
		const plan = toPlan(model)
		expect(plan.pages.slice(0, 2)).toEqual([{ source: 0, page: 2, rotate: 0 }, { source: 0, page: 1, rotate: 90 }])
		expect(plan.overlays).toEqual([{ page: 1, items: [text] }])
	})
	it('carries the form values only when there are some', () => {
		const model = addFile(emptyModel(), { source: 0, name: 'f.pdf', pageCount: 1 })
		expect(toPlan(model).formValues).toBeUndefined()
		expect(toPlan(setFormValue(model, 'name', 'Zed')).formValues).toEqual({ name: 'Zed' })
	})
})

describe('the form after pages were deleted', () => {
	const fields = [
		{ name: 'a', kind: 'text', value: '', readOnly: false, pages: [1] },
		{ name: 'b', kind: 'text', value: '', readOnly: false, pages: [2] },
		{ name: 'c', kind: 'radio', value: '', readOnly: false, pages: [1, 2] },
	] as const
	it('lists the fields with a widget on a page that stays', () => {
		let model = addFile(emptyModel(), { source: 0, name: 'f.pdf', pageCount: 2 })
		expect(fieldsOnKeptPages(model, 0, fields).map((f) => f.name)).toEqual(['a', 'b', 'c'])
		model = deletePages(model, [model.pages[1]!.key])
		expect(fieldsOnKeptPages(model, 0, fields).map((f) => f.name)).toEqual(['a', 'c'])
	})
	it('lists none when every page with a widget is gone', () => {
		let model = addFile(emptyModel(), { source: 0, name: 'f.pdf', pageCount: 3 })
		model = deletePages(model, [model.pages[0]!.key, model.pages[1]!.key])
		expect(fieldsOnKeptPages(model, 0, fields)).toEqual([])
	})
	it('takes the values of fields that are gone out of a plan', () => {
		const plan = { pages: [], overlays: [], formValues: { a: '1', b: '2' } }
		expect(onlyFields(plan, ['a']).formValues).toEqual({ a: '1' })
		expect(onlyFields(plan, []).formValues).toBeUndefined()
	})
})

describe('the plan for a range', () => {
	it('keeps the pages in the range, with their items renumbered', () => {
		let model = twoFiles()
		model = addItem(model, model.pages[3]!.key, text)
		const part = toPlanRange(model, { from: 3, to: 5 }, 1, [])
		expect(part.pages).toEqual([{ source: 1, page: 1, rotate: 0 }, { source: 1, page: 2, rotate: 0 }, { source: 1, page: 3, rotate: 0 }])
		expect(part.overlays).toEqual([{ page: 2, items: [text] }])
		expect(part.formValues).toBeUndefined()
		expect(toPlanRange(model, { from: 1, to: 2 }, 0, []).overlays).toEqual([])
	})
	it('carries the typed values of the fields that have a widget on a page in the range', () => {
		const fields = [
			{ name: 'first', pages: [1] },
			{ name: 'second', pages: [2] },
			{ name: 'both', pages: [1, 3] },
		]
		let model = addFile(emptyModel(), { source: 0, name: 'f.pdf', pageCount: 3 })
		model = setFormValue(setFormValue(setFormValue(model, 'first', 'Alice'), 'second', 'Bob'), 'both', 'yes')
		expect(toPlanRange(model, { from: 1, to: 1 }, 0, fields).formValues).toEqual({ first: 'Alice', both: 'yes' })
		expect(toPlanRange(model, { from: 2, to: 2 }, 0, fields).formValues).toEqual({ second: 'Bob' })
		expect(toPlanRange(model, { from: 1, to: 1 }, 0, []).formValues).toBeUndefined()
	})
})

describe('undo', () => {
	const grow = (h: History, n: number): History => {
		for (let i = 0; i < n; i++) h = commit(h, setFormValue(h.present, 'k', String(i)))
		return h
	}
	it('takes back and redoes edits, and a new edit clears what could be redone', () => {
		let h = startHistory(twoFiles())
		const original = h.present
		h = commit(h, rotatePages(h.present, [h.present.pages[0]!.key], 90))
		h = commit(h, deletePages(h.present, [h.present.pages[1]!.key]))
		h = undo(h)
		expect(h.present.pages).toHaveLength(5)
		h = undo(h)
		expect(h.present).toBe(original)
		expect(undo(h)).toBe(h)
		h = redo(h)
		expect(h.present.pages[0]!.rotate).toBe(90)
		h = commit(h, movePage(h.present, h.present.pages[0]!.key, 1))
		expect(redo(h)).toBe(h)
	})
	it('amends the present without a new step', () => {
		let h = startHistory(twoFiles())
		h = commit(h, setFormValue(h.present, 'k', '1'))
		const steps = h.past.length
		h = amend(h, setFormValue(h.present, 'k', '2'))
		expect(h.past).toHaveLength(steps)
		expect(h.present.form).toEqual({ k: '2' })
	})
	it('keeps at most the step limit', () => {
		const h = grow(startHistory(emptyModel()), PDF_LIMITS.undoSteps + 30)
		expect(h.past).toHaveLength(PDF_LIMITS.undoSteps)
	})
	it('lets go of the oldest steps when the images they hold are over the byte limit', () => {
		let h = startHistory(addFile(emptyModel(), { source: 0, name: 'a.pdf', pageCount: 1 }))
		const page = h.present.pages[0]!.key
		const picture = (): Uint8Array => new Uint8Array(PDF_LIMITS.maxOverlayImageBytes)
		for (let i = 0; i < 12; i++) {
			h = commit(h, addItem(h.present, page, { kind: 'image', bytes: picture(), format: 'png', x: 0, y: 0, width: 10, height: 10 }))
			h = commit(h, removeItem(h.present, h.present.items[0]!.id))
		}
		const held = new Set<Uint8Array>()
		for (const step of [...h.past, h.present, ...h.future]) for (const placed of step.items) if (placed.item.kind === 'image') held.add(placed.item.bytes)
		expect([...held].reduce((sum, bytes) => sum + bytes.length, 0)).toBeLessThanOrEqual(PDF_LIMITS.undoBytes)
		expect(h.past.length).toBeLessThan(24)
		expect(h.past.length).toBeGreaterThan(0)
	})
	it('refuses more pictures on the pages than the byte limit allows', () => {
		let model = addFile(emptyModel(), { source: 0, name: 'a.pdf', pageCount: 1 })
		const page = model.pages[0]!.key
		const picture = { kind: 'image', bytes: new Uint8Array(PDF_LIMITS.maxOverlayImageBytes), format: 'png', x: 0, y: 0, width: 10, height: 10 } as const
		for (let i = 0; i < PDF_LIMITS.undoBytes / PDF_LIMITS.maxOverlayImageBytes; i++) model = addItem(model, page, picture)
		expect(() => addItem(model, page, picture)).toThrow(/pictures/)
	})
	it('trim never drops the present', () => {
		const h = trim({ past: [], present: emptyModel(), future: [] })
		expect(h.present).toBeDefined()
	})
})
