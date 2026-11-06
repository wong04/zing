// Merging, splitting and editing PDFs with pdf-lib (MIT, 1.17.1). It works on bytes and never touches the disk: the panel
// reads a granted file through the host, runs these in a worker, and saves the result through the host.
//
// Every function loads its input afresh and returns new bytes; none changes what it was given. A file that is encrypted,
// damaged, empty or past PDF_LIMITS.maxPages is refused with a PdfEditError naming it. `ignoreEncryption` is never used:
// pdf-lib would then write the file without its encryption, which is not what the person asked for.
import { EncryptedPDFError, PDFArray, PDFContext, PDFDict, PDFDocument, PDFHexString, PDFName, PDFObjectCopier, PDFPage, PDFPageLeaf, PDFRef, PDFStream, PDFString, PDFCheckBox, PDFDropdown, PDFOptionList, PDFRadioGroup, PDFTextField, StandardFonts, degrees, rgb, type PDFFont, type PDFObject } from 'pdf-lib'
import { checkPictureSize } from './pictureHeader'
import {
	PDF_LIMITS,
	PdfEditError,
	asPdfFile,
	pageSizeFor,
	visualToUser,
	type FormFieldInfo,
	type OverlayItem,
	type PageRange,
	type PdfFile,
	type PdfInfo,
	type PdfOverlay,
	type PdfPlan,
	type PdfSource,
	type PictureFile,
	type PlannedPage,
	type RightAngle,
} from './pdfCore'

export { PDF_LIMITS, PdfEditError, pageSizeFor, parsePageRanges, visualToUser } from './pdfCore'
export type { FormFieldInfo, FormFieldKind, ImageItem, OverlayItem, PageBox, PageRange, PdfFile, PdfInfo, PdfOverlay, PdfPlan, PdfSource, PictureFile, PlannedPage, RgbColor, RightAngle, TextItem } from './pdfCore'

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
	// Which page each widget is on: found from the pages' annotations, the same way rebuildForm finds the widgets that stay.
	const pageOf = new Map<unknown, number>()
	for (const [index, page] of doc.getPages().entries()) {
		for (const entry of page.node.Annots()?.asArray() ?? []) {
			const widget = entry instanceof PDFRef ? doc.context.lookup(entry) : entry
			if (widget instanceof PDFDict) pageOf.set(widget, index + 1)
		}
	}
	for (const field of doc.getForm().getFields()) {
		const name = field.getName()
		const readOnly = field.isReadOnly()
		const pages = [...new Set(field.acroField.getWidgets().map((w) => pageOf.get(w.dict)).filter((p): p is number => p !== undefined))].sort((a, b) => a - b)
		if (field instanceof PDFTextField) fields.push({ name, kind: 'text', value: field.getText() ?? '', readOnly, pages })
		else if (field instanceof PDFCheckBox) fields.push({ name, kind: 'checkbox', value: String(field.isChecked()), readOnly, pages })
		else if (field instanceof PDFRadioGroup) fields.push({ name, kind: 'radio', value: field.getSelected() ?? '', options: field.getOptions(), readOnly, pages })
		else if (field instanceof PDFDropdown) fields.push({ name, kind: 'dropdown', value: field.getSelected()[0] ?? '', options: field.getOptions(), readOnly, pages })
		else if (field instanceof PDFOptionList) fields.push({ name, kind: 'list', value: field.getSelected()[0] ?? '', options: field.getOptions(), readOnly, pages })
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
	let doc: PDFDocument
	if (single && keepsEveryPageOnce(plan, docs[only]!)) {
		doc = reorderInPlace(docs[only]!, plan)
	} else {
		doc = await copyPlanned(docs, plan)
		if (single) rebuildForm(doc, docs[only]!)
		removeLostPages(doc)
	}
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

/** The destinations an action (and the actions it chains to) or a /Dest names, as page references. */
function targetsOf(dict: PDFDict): PDFRef[] {
	const found: PDFRef[] = []
	const dest = dict.lookup(PDFName.of('Dest'))
	if (dest instanceof PDFArray && dest.get(0) instanceof PDFRef) found.push(dest.get(0) as PDFRef)
	const pending: unknown[] = [dict.lookup(PDFName.of('A'))]
	for (let hops = 0; pending.length > 0 && hops < 100; hops++) {
		const action = pending.pop()
		if (action instanceof PDFArray) {
			// /Next is one action or an array of them.
			for (let i = 0; i < action.size(); i++) pending.push(action.lookup(i))
		} else if (action instanceof PDFDict) {
			const d = action.lookup(PDFName.of('D'))
			if (d instanceof PDFArray && d.get(0) instanceof PDFRef) found.push(d.get(0) as PDFRef)
			pending.push(action.lookup(PDFName.of('Next')))
		}
	}
	return found
}

/**
 * copyPages follows a link to the page it points at and copies that page too: a deleted page would stay in the file as an
 * orphan, and in a merge a kept page would be copied twice. So before copying, every annotation on a kept page that points
 * at a page (a link's /Dest, or a GoTo action) is dropped. Links to the web and form widgets are kept.
 */
function dropPageLinks(doc: PDFDocument, keep: ReadonlySet<number>): void {
	for (const [i, page] of doc.getPages().entries()) {
		if (!keep.has(i + 1)) continue
		const annots = page.node.Annots()
		if (annots === undefined) continue
		for (let at = annots.size() - 1; at >= 0; at--) {
			const annot = annots.lookupMaybe(at, PDFDict)
			if (annot !== undefined && targetsOf(annot).length > 0) annots.remove(at)
		}
	}
}

/**
 * Like copyPages, except that each page is copied through its reference. copyPages copies the page object itself, so a kept page
 * that something on another page points at (a named destination, an action) would come out twice, and that reference would lead
 * to the second copy; here it leads to the kept page.
 */
async function copyPagesByRef(out: PDFDocument, doc: PDFDocument, indices: readonly number[]): Promise<PDFPage[]> {
	await doc.flush()
	const copier = PDFObjectCopier.for(doc.context, out.context)
	const pages = doc.getPages()
	return indices.map((index) => {
		const ref = copier.copy(pages[index]!.ref)
		const leaf = out.context.lookup(ref)
		if (!(leaf instanceof PDFPageLeaf)) throw new PdfEditError(`Page ${index + 1} could not be copied.`)
		return PDFPage.of(leaf, ref, out)
	})
}

async function copyPlanned(docs: readonly PDFDocument[], plan: PdfPlan): Promise<PDFDocument> {
	const out = await PDFDocument.create()
	// One copyPages call per file, so what pages share (fonts, images) is copied once; a page used twice is copied again.
	const copies = new Map<string, PDFPage>()
	for (const [source, doc] of docs.entries()) {
		if (doc === undefined) continue
		const wanted = [...new Set(plan.pages.filter((p) => p.source === source).map((p) => p.page))]
		dropPageLinks(doc, new Set(wanted))
		const copied = await copyPagesByRef(out, doc, wanted.map((page) => page - 1))
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

const PAGE = PDFName.of('Type')
const isPageObject = (object: unknown): object is PDFDict => object instanceof PDFDict && object.get(PAGE) === PDFName.of('Page')

/**
 * A rebuilt file keeps the form for fields that still have a widget on a page that stays: the fields are found from the kept
 * pages' widgets and a new /AcroForm is made for them. A field whose widgets are all on deleted pages is not reached, so it and
 * its value are gone. (A merge has several files' forms and does not call this.)
 */
function rebuildForm(out: PDFDocument, source: PDFDocument): void {
	const sourceForm = source.catalog.lookupMaybe(PDFName.of('AcroForm'), PDFDict)
	if (sourceForm === undefined) return
	const roots: PDFRef[] = []
	for (const page of out.getPages()) {
		const annots = page.node.Annots()
		if (annots === undefined) continue
		for (const entry of annots.asArray()) {
			if (!(entry instanceof PDFRef)) continue
			let at = entry
			for (let hops = 0; hops < 50; hops++) {
				const dict = out.context.lookupMaybe(at, PDFDict)
				const parent = dict?.get(PDFName.of('Parent'))
				if (dict === undefined || !(parent instanceof PDFRef)) break
				at = parent
			}
			const widget = out.context.lookupMaybe(entry, PDFDict)
			const isWidget = widget?.get(PDFName.of('Subtype')) === PDFName.of('Widget')
			if (isWidget && !roots.includes(at)) roots.push(at)
		}
	}
	if (roots.length === 0) return
	const form = out.context.obj({ Fields: roots })
	const defaults = sourceForm.get(PDFName.of('DA'))
	if (defaults instanceof PDFString || defaults instanceof PDFHexString) form.set(PDFName.of('DA'), defaults)
	out.catalog.set(PDFName.of('AcroForm'), out.context.register(form))
}

const DEST = PDFName.of('Dest')
const DESTS = PDFName.of('Dests')
const NAMES = PDFName.of('Names')
const KIDS = PDFName.of('Kids')
const LIMITS = PDFName.of('Limits')
const FITS = new Set(['XYZ', 'Fit', 'FitH', 'FitV', 'FitR', 'FitB', 'FitBH', 'FitBV'].map((fit) => PDFName.of(fit)))

/**
 * A destination that leads to no page, directly or by reference: [lostPage /Fit], or [/Fit] once its page reference was cut
 * (objects are cut in no set order, so a destination that is its own object may already have lost its page).
 */
function isDeadDestination(value: unknown, lost: ReadonlySet<PDFRef>, context: PDFContext): boolean {
	if (value instanceof PDFRef && lost.has(value)) return true
	const dest = value instanceof PDFRef ? context.lookup(value) : value
	if (!(dest instanceof PDFArray)) return false
	const first = dest.get(0)
	return (first instanceof PDFRef && lost.has(first)) || (first instanceof PDFName && FITS.has(first))
}

/** An action that goes to no page: it would be left as [/Fit], so it is dropped whole. */
function isDeadAction(value: unknown, lost: ReadonlySet<PDFRef>, context: PDFContext): boolean {
	const dict = value instanceof PDFRef ? context.lookup(value) : value
	return dict instanceof PDFDict && isDeadDestination(dict.get(PDFName.of('D')), lost, context)
}

function isCut(value: unknown, lost: ReadonlySet<PDFRef>, context: PDFContext): boolean {
	return (value instanceof PDFRef && lost.has(value)) || isDeadAction(value, lost, context)
}

/** A leaf (with /Names) or an intermediate node (whose kids have /Limits) of a name tree. Page tree and field /Kids have no /Limits. */
function isNameTreeNode(dict: PDFDict): boolean {
	if (dict.lookup(NAMES) instanceof PDFArray) return true
	const kids = dict.lookup(KIDS)
	if (!(kids instanceof PDFArray) || kids.size() === 0) return false
	const first = kids.lookup(0)
	return first instanceof PDFDict && first.has(LIMITS)
}

/**
 * In a name tree a name and its value are cut together, so the pairs stay in step; a kid left with no names is taken out of
 * /Kids, and each /Limits is set to the names that are left. Returns whether the node still has a name.
 */
function cutNameNode(node: PDFDict, lost: ReadonlySet<PDFRef>, context: PDFContext, seen: Set<PDFDict>): boolean {
	const pairs = node.lookup(NAMES)
	if (pairs instanceof PDFArray) {
		for (let at = (pairs.size() & ~1) - 2; at >= 0; at -= 2) {
			const value = pairs.get(at + 1)
			if (!isCut(value, lost, context) && !isDeadDestination(value, lost, context)) continue
			pairs.remove(at + 1)
			pairs.remove(at)
		}
		const last = (pairs.size() & ~1) - 2
		if (last < 0) return false
		setLimits(node, pairs.get(0), pairs.get(last), context)
		return true
	}
	const kids = node.lookup(KIDS)
	if (!(kids instanceof PDFArray)) return false
	for (let at = kids.size() - 1; at >= 0; at--) {
		const kid = kids.lookup(at)
		if (!(kid instanceof PDFDict) || seen.has(kid)) continue
		seen.add(kid)
		if (!cutNameNode(kid, lost, context, seen)) kids.remove(at)
	}
	if (kids.size() === 0) return false
	const low = kids.lookup(0, PDFDict).lookup(LIMITS, PDFArray).get(0)
	const high = kids.lookup(kids.size() - 1, PDFDict).lookup(LIMITS, PDFArray).get(1)
	setLimits(node, low, high, context)
	return true
}

/** Only a kid has /Limits; the root of a tree has none. */
function setLimits(node: PDFDict, low: PDFObject, high: PDFObject, context: PDFContext): void {
	if (node.has(LIMITS)) node.set(LIMITS, context.obj([low, high]))
}

/** An old-style /Dests dictionary loses each name whose destination leads to no page. */
function cutDests(dests: PDFDict, lost: ReadonlySet<PDFRef>, context: PDFContext): void {
	for (const [name, value] of dests.entries()) if (isDeadDestination(value, lost, context) || isDeadAction(value, lost, context)) dests.delete(name)
}

/**
 * Takes out of `object` (and the plain dictionaries and arrays inside it, not what its references lead to) every reference to a lost
 * object. A destination or an action that would be left with no page is removed whole instead of being left as [/Fit]: its key, its
 * place in an array, or its name and value in a name tree.
 */
function cutReferences(object: unknown, lost: ReadonlySet<PDFRef>, context: PDFContext): void {
	if (object instanceof PDFStream) {
		cutReferences(object.dict, lost, context)
		return
	}
	if (object instanceof PDFDict) {
		if (isNameTreeNode(object)) cutNameNode(object, lost, context, new Set([object]))
		for (const [key, value] of object.entries()) {
			if (isCut(value, lost, context) || (key === DEST && isDeadDestination(value, lost, context))) {
				object.delete(key)
				continue
			}
			const dests = key === DESTS ? context.lookup(value) : undefined
			if (dests instanceof PDFDict && !isNameTreeNode(dests)) cutDests(dests, lost, context)
			cutReferences(value, lost, context)
		}
		return
	}
	if (object instanceof PDFArray) {
		for (let at = object.size() - 1; at >= 0; at--) {
			const value = object.get(at)
			if (isCut(value, lost, context)) object.remove(at)
			else cutReferences(value, lost, context)
		}
	}
}

/**
 * The guarantee that a left-out page is not in the file, however it was reached (a link, an action, a field's other widgets, an
 * article bead, a named destination): after copying, every page object that is not one of the output's pages is found, every
 * reference to it, and to the widgets that sat on it, is cut, and whatever is no longer reachable from the document is removed
 * from the file.
 */
function removeLostPages(doc: PDFDocument): void {
	const { context } = doc
	const kept = new Set(doc.getPages().map((p) => p.ref))
	const lost = new Set<PDFRef>()
	for (const [ref, object] of context.enumerateIndirectObjects()) if (isPageObject(object) && !kept.has(ref)) lost.add(ref)
	if (lost.size === 0) return
	// An annotation or widget whose own page is lost goes with it, and so does its place in a field's /Kids.
	for (const [ref, object] of context.enumerateIndirectObjects()) {
		const page = object instanceof PDFDict ? object.get(PDFName.of('P')) : undefined
		if (page instanceof PDFRef && lost.has(page)) lost.add(ref)
	}
	// A name tree's /Names array that is its own object is cut through its node, in pairs, never on its own.
	const nameArrays = new Set<PDFRef>()
	for (const [, object] of context.enumerateIndirectObjects()) {
		const names = object instanceof PDFDict ? object.get(NAMES) : undefined
		if (names instanceof PDFRef) nameArrays.add(names)
	}
	for (const [ref, object] of context.enumerateIndirectObjects()) if (!lost.has(ref) && !nameArrays.has(ref)) cutReferences(object, lost, context)
	for (const ref of lost) context.delete(ref)
	removeUnreachable(doc)
}

function removeUnreachable(doc: PDFDocument): void {
	const { context } = doc
	const seen = new Set<PDFRef>()
	const pending: unknown[] = [context.trailerInfo.Root, context.trailerInfo.Info]
	while (pending.length > 0) {
		const object = pending.pop()
		if (object instanceof PDFRef) {
			if (seen.has(object)) continue
			seen.add(object)
			pending.push(context.lookup(object))
		} else if (object instanceof PDFStream) pending.push(object.dict)
		else if (object instanceof PDFDict) for (const [, value] of object.entries()) pending.push(value)
		else if (object instanceof PDFArray) pending.push(...object.asArray())
	}
	for (const [ref] of context.enumerateIndirectObjects()) if (!seen.has(ref)) context.delete(ref)
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
	checkPictureSize(`The ${item.format.toUpperCase()} picture`, item.bytes)
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
	if (!doc.catalog.has(PDFName.of('AcroForm'))) throw new PdfEditError(`${name} has no form field called "${entries[0]![0]}": it has no form to fill.`)
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

/** What the panel needs to know when a file is added; refuses a file that can't be edited, so that is found out at once. */
export async function inspectPdf(file: PdfSource): Promise<PdfInfo> {
	const doc = await load(file)
	return { pageCount: doc.getPageCount(), fields: await listFormFields(file) }
}

/** A new PDF with one page for each picture, in order, each page the size of its picture (see pageSizeFor). */
export async function imagesToPdf(pictures: readonly PictureFile[]): Promise<Uint8Array> {
	if (pictures.length === 0) throw new PdfEditError('Choose at least one picture.')
	if (pictures.length > PDF_LIMITS.maxPages) throw new PdfEditError(`A PDF can have up to ${PDF_LIMITS.maxPages} pages; ${pictures.length} pictures were given.`)
	const doc = await PDFDocument.create()
	for (const picture of pictures) {
		checkPictureSize(picture.name, picture.bytes)
		let image
		try {
			image = picture.format === 'png' ? await doc.embedPng(picture.bytes) : await doc.embedJpg(picture.bytes)
		} catch (error) {
			throw new PdfEditError(`${picture.name} can't be read as a ${picture.format.toUpperCase()} picture: ${error instanceof Error ? error.message : String(error)}`)
		}
		const size = pageSizeFor(image.width, image.height)
		doc.addPage([size.width, size.height]).drawImage(image, { x: 0, y: 0, width: size.width, height: size.height })
	}
	return doc.save()
}
