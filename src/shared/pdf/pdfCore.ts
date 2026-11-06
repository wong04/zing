// The parts of PDF editing that need neither pdf-lib nor pdf.js, so the panel can use them without loading either.

const MiB = 1024 * 1024

/**
 * What Zing will take on, stated so the panel and the tests say the same numbers. The two byte limits are also in the host
 * (host/Zing.Core/PdfFiles.cs); a test keeps them equal.
 */
export const PDF_LIMITS = {
	/** Pages in any one file, loaded or made. A bigger file is refused, not worked on until the window stalls. */
	maxPages: 2000,
	maxMergeFiles: 20,
	maxSplitParts: 50,
	/** One file read from disk through the bridge (it travels as base64, a third larger). */
	maxReadBytes: 50 * MiB,
	/** One file written to Downloads through the bridge. */
	maxSaveBytes: 100 * MiB,
	/** An image placed on a page (a picture or a signature). */
	maxOverlayImageBytes: 8 * MiB,
	/** Pages Convert turns into pictures in one run. */
	maxConvertPages: 100,
	/** All the files open in the panel together; they are held in memory until it is closed. */
	maxSessionBytes: 200 * MiB,
	/** Edits that Undo can take back. */
	undoSteps: 100,
	/** Image bytes the undo history may hold on to, over all its steps. */
	undoBytes: 64 * MiB,
} as const

export class PdfEditError extends Error {
	override name = 'PdfEditError'
}

/** Pages from `from` to `to`, counted from 1, both included. */
export interface PageRange {
	readonly from: number
	readonly to: number
}

/** A PDF and the name it is shown by, which error messages use. */
export interface PdfFile {
	readonly name: string
	readonly bytes: Uint8Array
}

export type PdfSource = Uint8Array | PdfFile

export function asPdfFile(source: PdfSource): PdfFile {
	return source instanceof Uint8Array ? { name: 'This PDF', bytes: source } : source
}

export type RgbColor = readonly [red: number, green: number, blue: number]

/** Text placed on a page. `x` and `y` are the top-left of the first line, in points on the page as the file shows it (its own turn included, but not the extra turn in a plan, which is applied after drawing), y downward. */
export interface TextItem {
	readonly kind: 'text'
	readonly text: string
	readonly x: number
	readonly y: number
	readonly size: number
	/** Each 0 to 1. Black when left out. */
	readonly color?: RgbColor
}

/** A PNG or JPEG placed on a page; the box is in points on the page as it is shown, y downward. A signature is one of these. */
export interface ImageItem {
	readonly kind: 'image'
	readonly bytes: Uint8Array
	readonly format: 'png' | 'jpg'
	readonly x: number
	readonly y: number
	readonly width: number
	readonly height: number
}

export type OverlayItem = TextItem | ImageItem

/** Things drawn on top of one page (counted from 1). */
export interface PdfOverlay {
	readonly page: number
	readonly items: readonly OverlayItem[]
}

export type RightAngle = 90 | 180 | 270

export interface PlannedPage {
	/** Index into the sources. */
	readonly source: number
	/** Counted from 1. */
	readonly page: number
	/** Turned this much further than the page already is. */
	readonly rotate: 0 | RightAngle
}

/** The finished file as the panel pictures it: which pages, in what order and turned how; what is drawn on them; the form's values. */
export interface PdfPlan {
	readonly pages: readonly PlannedPage[]
	/** `page` counts the finished file's pages. */
	readonly overlays: readonly PdfOverlay[]
	/** Only for a plan that uses one file. */
	readonly formValues?: Readonly<Record<string, string>>
}

export type FormFieldKind = 'text' | 'checkbox' | 'radio' | 'dropdown' | 'list'

export interface FormFieldInfo {
	readonly name: string
	readonly kind: FormFieldKind
	/** Text as it is; a checkbox "true" or "false"; a choice the option chosen; "" when empty. */
	readonly value: string
	readonly options?: readonly string[]
	readonly readOnly: boolean
}

/**
 * "1-3, 5" as ranges. Pages count from 1. A list of single pages and ranges, separated by commas, in the order given.
 * @throws PdfEditError for an empty list, anything that is not a number or a range, a backwards range, a page past the end,
 *         or more ranges than a split may make.
 */
export function parsePageRanges(input: string, pageCount: number): PageRange[] {
	if (input.trim() === '') throw new PdfEditError('Say which pages, such as "1-3, 5".')
	const ranges = input.split(',').map((part) => parseRange(part.trim(), pageCount))
	if (ranges.length > PDF_LIMITS.maxSplitParts) throw new PdfEditError(`A split makes at most ${PDF_LIMITS.maxSplitParts} files; ${ranges.length} were asked for.`)
	return ranges
}

function parseRange(part: string, pageCount: number): PageRange {
	const match = /^(\d+)(?:\s*-\s*(\d+))?$/.exec(part)
	if (match === null) throw new PdfEditError(`"${part}" isn't a page or a range such as 2-4.`)
	const from = Number(match[1])
	const to = match[2] === undefined ? from : Number(match[2])
	if (from < 1 || to < 1) throw new PdfEditError('Pages count from 1.')
	if (to < from) throw new PdfEditError(`"${part}" goes backwards; write it as ${to}-${from}.`)
	if (to > pageCount) throw new PdfEditError(`"${part}" is past the end: the file has ${pageCount} ${pageCount === 1 ? 'page' : 'pages'}.`)
	return { from, to }
}

export interface PageBox {
	readonly x: number
	readonly y: number
	readonly width: number
	readonly height: number
}

/**
 * Where a point is on the page's own coordinates (origin bottom left, y up) when it was picked on the page as shown:
 * origin top left, y down, the page turned clockwise by `rotation` (0, 90, 180 or 270). `box` is the unturned page.
 */
export function visualToUser(box: PageBox, rotation: number, vx: number, vy: number): { x: number; y: number } {
	switch (rotation) {
		case 0:
			return { x: box.x + vx, y: box.y + box.height - vy }
		case 90:
			return { x: box.x + vy, y: box.y + vx }
		case 180:
			return { x: box.x + box.width - vx, y: box.y + vy }
		case 270:
			return { x: box.x + box.width - vy, y: box.y + box.height - vx }
		default:
			throw new PdfEditError(`Zing can't place anything on a page turned ${rotation} degrees`)
	}
}
