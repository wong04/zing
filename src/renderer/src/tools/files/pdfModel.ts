// The PDF panel's edits, kept as a small description rather than as rewritten files: which pages in what order and turned
// how, what is drawn on them, and the form's values. The files themselves are never changed in memory; the finished PDF is
// built from this description when it is saved (src/shared/pdf/pdfEdit.ts). That is what makes Undo cheap and bounded.
import { PDF_LIMITS, PdfEditError, type OverlayItem, type PdfPlan, type RightAngle } from '@shared/pdf/pdfCore'

export interface FileInfo {
	readonly name: string
	readonly pageCount: number
	readonly bytes: number
}

export interface PageEntry {
	/** Identifies this page in the list for as long as it is there; unlike its position, it doesn't change when pages move. */
	readonly key: number
	readonly source: number
	/** Counted from 1 in its file. */
	readonly page: number
	/** Turned this much clockwise beyond the file's own turn. */
	readonly rotate: 0 | RightAngle
}

export interface PlacedItem {
	readonly id: number
	/** The key of the page it is on. */
	readonly page: number
	readonly item: OverlayItem
}

export interface Model {
	readonly files: readonly FileInfo[]
	readonly pages: readonly PageEntry[]
	readonly items: readonly PlacedItem[]
	readonly form: Readonly<Record<string, string>>
	readonly nextKey: number
}

export function emptyModel(): Model {
	return { files: [], pages: [], items: [], form: {}, nextKey: 1 }
}

/** Adds all of a file's pages at the end. */
export function addFile(model: Model, file: FileInfo): Model {
	const total = model.files.reduce((sum, f) => sum + f.bytes, 0) + file.bytes
	if (total > PDF_LIMITS.maxSessionBytes) throw new PdfEditError(`${file.name} would take the open files past ${PDF_LIMITS.maxSessionBytes / (1024 * 1024)} MB, which is all the memory the PDF tools use. Close the panel to start afresh.`)
	if (model.pages.length + file.pageCount > PDF_LIMITS.maxPages) throw new PdfEditError(`${file.name} would make ${model.pages.length + file.pageCount} pages; the PDF tools handle up to ${PDF_LIMITS.maxPages}.`)
	const source = model.files.length
	const added = Array.from({ length: file.pageCount }, (_, i): PageEntry => ({ key: model.nextKey + i, source, page: i + 1, rotate: 0 }))
	return { ...model, files: [...model.files, file], pages: [...model.pages, ...added], nextKey: model.nextKey + file.pageCount }
}

/** Moves a page one place earlier (-1) or later (1); the model is returned as it was when the page is at that end. */
export function movePage(model: Model, key: number, by: -1 | 1): Model {
	const at = model.pages.findIndex((p) => p.key === key)
	const to = at + by
	if (at < 0 || to < 0 || to >= model.pages.length) return model
	const pages = [...model.pages]
	;[pages[at], pages[to]] = [pages[to]!, pages[at]!]
	return { ...model, pages }
}

export function rotatePages(model: Model, keys: readonly number[], by: RightAngle): Model {
	const chosen = new Set(keys)
	return { ...model, pages: model.pages.map((p) => (chosen.has(p.key) ? { ...p, rotate: ((p.rotate + by) % 360) as PageEntry['rotate'] } : p)) }
}

/** @throws PdfEditError if it would leave no page. */
export function deletePages(model: Model, keys: readonly number[]): Model {
	const gone = new Set(keys)
	const pages = model.pages.filter((p) => !gone.has(p.key))
	if (pages.length === 0) throw new PdfEditError('A PDF needs at least one page; this would delete them all.')
	return { ...model, pages, items: model.items.filter((i) => !gone.has(i.page)) }
}

function imageBytes(items: readonly PlacedItem[]): number {
	let total = 0
	for (const { item } of items) if (item.kind === 'image') total += item.bytes.length
	return total
}

/** @throws PdfEditError for a picture that would take the pictures on the pages past the memory the undo history is held to. */
export function addItem(model: Model, page: number, item: OverlayItem): Model {
	if (item.kind === 'image' && imageBytes(model.items) + item.bytes.length > PDF_LIMITS.undoBytes) {
		throw new PdfEditError(`The pictures on the pages already take ${PDF_LIMITS.undoBytes / (1024 * 1024)} MB, which is as much as the PDF tools keep. Remove one first.`)
	}
	const id = model.nextKey
	return { ...model, items: [...model.items, { id, page, item }], nextKey: id + 1 }
}

export function moveItem(model: Model, id: number, to: { readonly x: number; readonly y: number }): Model {
	return replaceItem(model, id, (item) => ({ ...item, x: to.x, y: to.y }))
}

/** A text item's size in points, or an image's width (its height follows, keeping its shape). */
export function resizeItem(model: Model, id: number, size: number): Model {
	return replaceItem(model, id, (item) => (item.kind === 'text' ? { ...item, size } : { ...item, height: (item.height / item.width) * size, width: size }))
}

export function editText(model: Model, id: number, text: string): Model {
	return replaceItem(model, id, (item) => (item.kind === 'text' ? { ...item, text } : item))
}

function replaceItem(model: Model, id: number, change: (item: OverlayItem) => OverlayItem): Model {
	return { ...model, items: model.items.map((placed) => (placed.id === id ? { ...placed, item: change(placed.item) } : placed)) }
}

export function removeItem(model: Model, id: number): Model {
	return { ...model, items: model.items.filter((i) => i.id !== id) }
}

export function setFormValue(model: Model, name: string, value: string): Model {
	return { ...model, form: { ...model.form, [name]: value } }
}

/** The description pdfEdit.buildPdf takes. Items follow their page to its place in the result. */
export function toPlan(model: Model): PdfPlan {
	const overlays = model.pages.flatMap((entry, index) => {
		const items = model.items.filter((i) => i.page === entry.key).map((i) => i.item)
		return items.length === 0 ? [] : [{ page: index + 1, items }]
	})
	const pages = model.pages.map(({ source, page, rotate }) => ({ source, page, rotate }))
	return Object.keys(model.form).length === 0 ? { pages, overlays } : { pages, overlays, formValues: model.form }
}

export interface History {
	readonly past: readonly Model[]
	readonly present: Model
	readonly future: readonly Model[]
}

export function startHistory(model: Model): History {
	return { past: [], present: model, future: [] }
}

/** Makes `next` the present. What could have been redone is forgotten. */
export function commit(history: History, next: Model): History {
	if (next === history.present) return history
	return trim({ past: [...history.past, history.present], present: next, future: [] })
}

export function undo(history: History): History {
	const previous = history.past.at(-1)
	if (previous === undefined) return history
	return { past: history.past.slice(0, -1), present: previous, future: [history.present, ...history.future] }
}

export function redo(history: History): History {
	const [next, ...rest] = history.future
	if (next === undefined) return history
	return { past: [...history.past, history.present], present: next, future: rest }
}

function heldBytes(history: History): number {
	const seen = new Set<Uint8Array>()
	for (const step of [...history.past, history.present, ...history.future]) {
		for (const { item } of step.items) if (item.kind === 'image') seen.add(item.bytes)
	}
	let total = 0
	for (const bytes of seen) total += bytes.length
	return total
}

/** Keeps at most `undoSteps` edits to go back to, and lets go of the oldest while the pictures held across the history are over `undoBytes`. The present is never dropped. */
export function trim(history: History): History {
	let { past, future } = history
	if (past.length > PDF_LIMITS.undoSteps) past = past.slice(past.length - PDF_LIMITS.undoSteps)
	let current: History = { past, present: history.present, future }
	while (heldBytes(current) > PDF_LIMITS.undoBytes && (current.past.length > 0 || current.future.length > 0)) {
		current = current.past.length > 0 ? { ...current, past: current.past.slice(1) } : { ...current, future: current.future.slice(0, -1) }
	}
	past = current.past
	future = current.future
	return { past, present: history.present, future }
}
