// Merging, splitting and editing PDFs with pdf-lib (MIT, 1.17.1). It works on bytes and never touches the disk: the panel
// reads a granted file through the host, runs these in a worker, and saves the result through the host.
//
// Every function loads its input afresh and returns new bytes; none changes what it was given. A file that is encrypted,
// damaged, empty or past PDF_LIMITS.maxPages is refused with a PdfEditError naming it. `ignoreEncryption` is never used:
// pdf-lib would then write the file without its encryption, which is not what the person asked for.
import { EncryptedPDFError, PDFDocument, PDFName, PDFCheckBox, PDFDropdown, PDFOptionList, PDFRadioGroup, PDFTextField, StandardFonts, degrees, rgb, type PDFFont, type PDFPage } from 'pdf-lib'
import {
	PDF_LIMITS,
	PdfEditError,
	asPdfFile,
	visualToUser,
	type FormFieldInfo,
	type OverlayItem,
	type PageRange,
	type PdfFile,
	type PdfOverlay,
	type PdfPlan,
	type PdfSource,
	type PlannedPage,
	type RightAngle,
} from './pdfCore'

export { PDF_LIMITS, PdfEditError, parsePageRanges, visualToUser } from './pdfCore'
export type { FormFieldInfo, FormFieldKind, ImageItem, OverlayItem, PageBox, PageRange, PdfFile, PdfOverlay, PdfPlan, PdfSource, PlannedPage, RgbColor, RightAngle, TextItem } from './pdfCore'

async function load(source: PdfSource): Promise<PDFDocument> {
	const { name, bytes } = asPdfFile(source)
	if (bytes.length === 0) throw new PdfEditError(`${name} is empty.`)
	let doc: PDFDocument
	let count: number
	try {
		doc = await PDFDocument.load(bytes, { updateMetadata: false })
		count = doc.getPageCount()
	} catch (error) {
		// pdf-lib's classes are compiled for old engines, where `instanceof` can fail for an Error subclass, so its message is matched too (the encrypted fixture in the tests catches a change).
		if (error instanceof EncryptedPDFError || (error instanceof Error && error.message.includes('is encrypted'))) {
			throw new PdfEditError(`${name} is protected by a password, which Zing can't remove. Open it with its password elsewhere and save an unprotected copy first.`)
		}
		throw new PdfEditError(`${name} isn't a PDF Zing can read; it may be damaged. (${error instanceof Error ? error.message : String(error)})`)
	}
	if (count === 0) throw new PdfEditError(`${name} has no pages Zing can read; it may be damaged.`)
	if (count > PDF_LIMITS.maxPages) throw new PdfEditError(`${name} has ${count} pages; Zing works on files of up to ${PDF_LIMITS.maxPages} pages.`)
	return doc
}

const normalAngle = (angle: number): number => ((angle % 360) + 360) % 360

function checkPages(name: string, pages: readonly number[], pageCount: number): number[] {
	const unique = [...new Set(pages)].sort((a, b) => a - b)
	for (const page of unique) {
		if (!Number.isInteger(page) || page < 1 || page > pageCount) throw new PdfEditError(`${name} has no page ${page}; it has ${pageCount}.`)
	}
	return unique
}

/** Pages 1 to `count`, each as it is. */
function wholeFile(count: number, source = 0): PlannedPage[] {
	return Array.from({ length: count }, (_, i) => ({ source, page: i + 1, rotate: 0 }))
}

/** Joins the files, one after another. Bookmarks, form fields and document properties of the files are not carried over (pdf-lib copies pages, not the document around them). */
export async function mergePdfs(files: readonly PdfSource[]): Promise<Uint8Array> {
	if (files.length < 2) throw new PdfEditError('Choose at least two PDFs to merge.')
	if (files.length > PDF_LIMITS.maxMergeFiles) throw new PdfEditError(`Zing merges at most ${PDF_LIMITS.maxMergeFiles} files at once; ${files.length} were given.`)
	const sources = files.map(asPdfFile)
	const docs = await Promise.all(sources.map(load))
	const total = docs.reduce((sum, doc) => sum + doc.getPageCount(), 0)
	if (total > PDF_LIMITS.maxPages) throw new PdfEditError(`These files have ${total} pages together; Zing makes files of up to ${PDF_LIMITS.maxPages} pages.`)
	return assemble(sources, docs, { pages: docs.flatMap((doc, source) => wholeFile(doc.getPageCount(), source)), overlays: [] })
}

/** One new file for each range. Like merging, this drops bookmarks and form fields. */
export async function splitPdf(file: PdfSource, ranges: readonly PageRange[]): Promise<Uint8Array[]> {
	const source = asPdfFile(file)
	if (ranges.length === 0) throw new PdfEditError('Say which pages to split out.')
	if (ranges.length > PDF_LIMITS.maxSplitParts) throw new PdfEditError(`A split makes at most ${PDF_LIMITS.maxSplitParts} files.`)
	const count = (await load(source)).getPageCount()
	const parts: Uint8Array[] = []
	for (const { from, to } of ranges) {
		if (from < 1 || to < from || to > count) throw new PdfEditError(`${source.name} has no pages ${from} to ${to}; it has ${count}.`)
		const pages = Array.from({ length: to - from + 1 }, (_, i) => ({ source: 0, page: from + i, rotate: 0 as const }))
		parts.push(await buildPdf([source], { pages, overlays: [] }))
	}
	return parts
}

/** `order` lists the pages in their new places, each of 1 to the page count exactly once. */
export async function reorderPages(file: PdfSource, order: readonly number[]): Promise<Uint8Array> {
	const source = asPdfFile(file)
	const count = (await load(source)).getPageCount()
	const sorted = [...order].sort((a, b) => a - b)
	if (order.length !== count || sorted.some((page, i) => page !== i + 1)) {
		throw new PdfEditError(`The new order must list each of the ${count} pages of ${source.name} exactly once.`)
	}
	return buildPdf([source], { pages: order.map((page) => ({ source: 0, page, rotate: 0 })), overlays: [] })
}

/** Turns the pages clockwise by `angle` more than they are turned now. */
export async function rotatePages(file: PdfSource, pages: readonly number[], angle: RightAngle): Promise<Uint8Array> {
	const source = asPdfFile(file)
	const count = (await load(source)).getPageCount()
	const chosen = new Set(checkPages(source.name, pages, count))
	return buildPdf([source], { pages: wholeFile(count).map((p) => ({ ...p, rotate: chosen.has(p.page) ? angle : 0 })), overlays: [] })
}

/** Removes pages for good: the finished file is built from the pages that stay, so the others' content is not in it. */
export async function deletePages(file: PdfSource, pages: readonly number[]): Promise<Uint8Array> {
	const source = asPdfFile(file)
	const count = (await load(source)).getPageCount()
	const gone = new Set(checkPages(source.name, pages, count))
	if (gone.size === count) throw new PdfEditError(`That would delete every page of ${source.name}.`)
	return buildPdf([source], { pages: wholeFile(count).filter((p) => !gone.has(p.page)), overlays: [] })
}

/** Draws the overlay's text and images on its page. The page's own content is kept as it is. */
export async function addOverlay(file: PdfSource, overlay: PdfOverlay): Promise<Uint8Array> {
	const source = asPdfFile(file)
	const doc = await load(source)
	checkPages(source.name, [overlay.page], doc.getPageCount())
	await draw(doc, source.name, overlay)
	return doc.save()
}

/** Sets form fields by name. The form stays a form: fields remain fillable afterwards (the file is not flattened). */
export async function fillForm(file: PdfSource, values: Readonly<Record<string, string>>): Promise<Uint8Array> {
	const source = asPdfFile(file)
	const doc = await load(source)
	await fill(doc, source.name, values)
	return doc.save()
}

/** The fields a person can fill: text, check boxes, radio groups, drop-downs and lists. Buttons and signature fields are left out. */
export async function listFormFields(file: PdfSource): Promise<FormFieldInfo[]> {
	const doc = await load(file)
	// getForm() makes an empty form in a file that has none; asking first leaves the file as it is.
	if (!doc.catalog.has(PDFName.of('AcroForm'))) return []
	const fields: FormFieldInfo[] = []
	for (const field of doc.getForm().getFields()) {
		const name = field.getName()
		const readOnly = field.isReadOnly()
		if (field instanceof PDFTextField) fields.push({ name, kind: 'text', value: field.getText() ?? '', readOnly })
		else if (field instanceof PDFCheckBox) fields.push({ name, kind: 'checkbox', value: String(field.isChecked()), readOnly })
		else if (field instanceof PDFRadioGroup) fields.push({ name, kind: 'radio', value: field.getSelected() ?? '', options: field.getOptions(), readOnly })
		else if (field instanceof PDFDropdown) fields.push({ name, kind: 'dropdown', value: field.getSelected()[0] ?? '', options: field.getOptions(), readOnly })
		else if (field instanceof PDFOptionList) fields.push({ name, kind: 'list', value: field.getSelected()[0] ?? '', options: field.getOptions(), readOnly })
	}
	return fields
}

/**
 * Makes the finished file from a plan. When it uses one file whole (only reordered, turned, drawn on or filled in), that
 * file is edited itself, so its bookmarks and form fields stay. Otherwise (several files, or pages left out) the pages are
 * copied into a new file: that drops bookmarks and form fields, and means a left-out page's content is not in the result.
 */
export async function buildPdf(files: readonly PdfSource[], plan: PdfPlan): Promise<Uint8Array> {
	const sources = files.map(asPdfFile)
	validatePlan(sources, plan)
	const used = [...new Set(plan.pages.map((p) => p.source))]
	const docs: PDFDocument[] = []
	for (const index of used) docs[index] = await load(sources[index]!)
	return assemble(sources, docs, plan)
}

function validatePlan(sources: readonly PdfFile[], plan: PdfPlan): void {
	if (plan.pages.length === 0) throw new PdfEditError('The finished file would have no pages.')
	if (plan.pages.length > PDF_LIMITS.maxPages) throw new PdfEditError(`The finished file would have ${plan.pages.length} pages; Zing makes files of up to ${PDF_LIMITS.maxPages} pages.`)
	for (const entry of plan.pages) {
		if (sources[entry.source] === undefined) throw new PdfEditError(`The plan names file ${entry.source}, which was not given.`)
		if (![0, 90, 180, 270].includes(entry.rotate)) throw new PdfEditError(`Pages turn by 90, 180 or 270 degrees, not ${entry.rotate}.`)
	}
	if (plan.formValues !== undefined && Object.keys(plan.formValues).length > 0 && new Set(plan.pages.map((p) => p.source)).size > 1) {
		throw new PdfEditError('A form can only be filled in a single file, not in pages gathered from several.')
	}
}

async function assemble(sources: readonly PdfFile[], docs: readonly PDFDocument[], plan: PdfPlan): Promise<Uint8Array> {
	validatePlan(sources, plan)
	for (const entry of plan.pages) {
		const doc = docs[entry.source]!
		checkPages(sources[entry.source]!.name, [entry.page], doc.getPageCount())
	}
	const only = plan.pages[0]!.source
	const single = plan.pages.every((p) => p.source === only)
	const doc = single && keepsEveryPageOnce(plan, docs[only]!) ? reorderInPlace(docs[only]!, plan) : await copyPlanned(docs, plan)
	// Drawn before the pages are turned, so what is placed turns with its page.
	for (const overlay of plan.overlays) {
		checkPages('The finished file', [overlay.page], doc.getPageCount())
		await draw(doc, sources[only]!.name, overlay)
	}
	turnPages(doc, plan)
	if (plan.formValues !== undefined && Object.keys(plan.formValues).length > 0) await fill(doc, sources[only]!.name, plan.formValues)
	return doc.save()
}

function keepsEveryPageOnce(plan: PdfPlan, doc: PDFDocument): boolean {
	if (plan.pages.length !== doc.getPageCount()) return false
	return new Set(plan.pages.map((p) => p.page)).size === plan.pages.length
}

function reorderInPlace(doc: PDFDocument, plan: PdfPlan): PDFDocument {
	const pages = doc.getPages()
	const ordered = plan.pages.map((entry) => pages[entry.page - 1]!)
	for (let i = pages.length - 1; i >= 0; i--) doc.removePage(i)
	for (const page of ordered) doc.addPage(page)
	return doc
}

function turnPages(doc: PDFDocument, plan: PdfPlan): void {
	doc.getPages().forEach((page, i) => {
		const turn = plan.pages[i]!.rotate
		if (turn !== 0) page.setRotation(degrees(normalAngle(page.getRotation().angle + turn)))
	})
}

async function copyPlanned(docs: readonly PDFDocument[], plan: PdfPlan): Promise<PDFDocument> {
	const out = await PDFDocument.create()
	// One copyPages call per file, so what pages share (fonts, images) is copied once; a page used twice is copied again.
	const copies = new Map<string, PDFPage>()
	for (const [source, doc] of docs.entries()) {
		if (doc === undefined) continue
		const wanted = [...new Set(plan.pages.filter((p) => p.source === source).map((p) => p.page))]
		const copied = await out.copyPages(doc, wanted.map((page) => page - 1))
		wanted.forEach((page, i) => copies.set(`${source}:${page}`, copied[i]!))
	}
	const used = new Set<PDFPage>()
	for (const entry of plan.pages) {
		let page = copies.get(`${entry.source}:${entry.page}`)!
		if (used.has(page)) page = (await out.copyPages(docs[entry.source]!, [entry.page - 1]))[0]!
		used.add(page)
		out.addPage(page)
	}
	return out
}

async function helvetica(doc: PDFDocument): Promise<PDFFont> {
	return doc.embedFont(StandardFonts.Helvetica)
}

function assertDrawable(font: PDFFont, text: string, what: string): void {
	try {
		font.encodeText(text)
	} catch {
		const bad = [...text].find((ch) => {
			try {
				font.encodeText(ch)
				return false
			} catch {
				return true
			}
		})
		throw new PdfEditError(`${what} has "${bad ?? text}", which the built-in Helvetica font can't draw. Zing writes Western European letters, digits and punctuation only.`)
	}
}

async function draw(doc: PDFDocument, name: string, overlay: PdfOverlay): Promise<void> {
	if (overlay.items.length === 0) return
	const page = doc.getPage(overlay.page - 1)
	const rotation = normalAngle(page.getRotation().angle)
	if (rotation % 90 !== 0) throw new PdfEditError(`Page ${overlay.page} of ${name} is turned ${rotation} degrees, which Zing can't draw on.`)
	const box = page.getMediaBox()
	const font = overlay.items.some((item) => item.kind === 'text') ? await helvetica(doc) : undefined
	for (const item of overlay.items) {
		if (item.kind === 'text') drawText(page, font!, item, box, rotation)
		else await drawImage(doc, page, item, box, rotation)
	}
}

function finite(...numbers: number[]): boolean {
	return numbers.every(Number.isFinite)
}

function drawText(page: PDFPage, font: PDFFont, item: Extract<OverlayItem, { kind: 'text' }>, box: { x: number; y: number; width: number; height: number }, rotation: number): void {
	if (!finite(item.x, item.y, item.size) || item.size <= 0) throw new PdfEditError('Text needs a position and a size above zero.')
	assertDrawable(font, item.text.replace(/\r?\n/g, ' '), 'The text')
	const ascent = font.heightAtSize(item.size, { descender: false })
	const [red, green, blue] = item.color ?? [0, 0, 0]
	item.text.split(/\r?\n/).forEach((line, i) => {
		const at = visualToUser(box, rotation, item.x, item.y + ascent + i * item.size * 1.2)
		page.drawText(line, { x: at.x, y: at.y, size: item.size, font, color: rgb(red, green, blue), rotate: degrees(rotation) })
	})
}

async function drawImage(doc: PDFDocument, page: PDFPage, item: Extract<OverlayItem, { kind: 'image' }>, box: { x: number; y: number; width: number; height: number }, rotation: number): Promise<void> {
	if (!finite(item.x, item.y, item.width, item.height) || item.width <= 0 || item.height <= 0) throw new PdfEditError('An image needs a position and a size above zero.')
	let image
	try {
		image = item.format === 'png' ? await doc.embedPng(item.bytes) : await doc.embedJpg(item.bytes)
	} catch (error) {
		throw new PdfEditError(`The ${item.format.toUpperCase()} image can't be read: ${error instanceof Error ? error.message : String(error)}`)
	}
	// The image's bottom-left corner on the page as shown, then in the page's own space.
	const at = visualToUser(box, rotation, item.x, item.y + item.height)
	page.drawImage(image, { x: at.x, y: at.y, width: item.width, height: item.height, rotate: degrees(rotation) })
}

async function fill(doc: PDFDocument, name: string, values: Readonly<Record<string, string>>): Promise<void> {
	const entries = Object.entries(values)
	if (entries.length === 0) return
	if (!doc.catalog.has(PDFName.of('AcroForm'))) throw new PdfEditError(`${name} has no form to fill.`)
	const form = doc.getForm()
	const font = await helvetica(doc)
	for (const [field, value] of entries) {
		const target = form.getFieldMaybe(field)
		if (target === undefined) throw new PdfEditError(`${name} has no form field called "${field}".`)
		if (target instanceof PDFTextField) {
			assertDrawable(font, value, `The text for "${field}"`)
			target.setText(value)
		} else if (target instanceof PDFCheckBox) {
			if (value !== 'true' && value !== 'false') throw new PdfEditError(`The check box "${field}" takes true or false, not "${value}".`)
			if (value === 'true') target.check()
			else target.uncheck()
		} else if (target instanceof PDFRadioGroup || target instanceof PDFDropdown || target instanceof PDFOptionList) {
			if (!target.getOptions().includes(value)) throw new PdfEditError(`"${value}" isn't one of the choices for "${field}".`)
			target.select(value)
		} else {
			throw new PdfEditError(`"${field}" is not a field Zing can fill.`)
		}
	}
	form.updateFieldAppearances(font)
}
