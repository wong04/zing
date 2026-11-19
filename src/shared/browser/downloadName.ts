import { safeFileBase, safeFileExtension } from '../convert/commands'

/** Windows' longest file name, in UTF-16 units. */
const MAX_NAME = 255
/** Longer than any real extension: past this it is part of the name, and is cut so the name keeps room. */
const MAX_EXTENSION = 64

/**
 * The file name a download is saved under: made safe for Windows, at most 255 characters with its extension kept, and
 * numbered ("report 2.pdf") rather than overwriting a file that is already there.
 */
export function downloadFileName(filename: string, exists: (name: string) => boolean): string {
	const dot = filename.lastIndexOf('.')
	const hasExt = dot > 0 && dot < filename.length - 1
	const base = safeFileBase(hasExt ? filename.slice(0, dot) : filename)
	const cleanExt = hasExt ? cutTo(safeFileExtension(filename.slice(dot + 1)), MAX_EXTENSION) : ''
	const ext = cleanExt ? `.${cleanExt}` : ''
	let candidate = fitted(base, ext)
	for (let counter = 2; exists(candidate); counter++) candidate = fitted(base, ` ${counter}${ext}`)
	return candidate
}

function fitted(base: string, tail: string): string {
	if (base.length + tail.length <= MAX_NAME) return base + tail
	return (cutTo(base, MAX_NAME - tail.length).replace(/[. ]+$/, '') || 'file') + tail
}

/** The first `length` UTF-16 units, without splitting a surrogate pair. */
function cutTo(text: string, length: number): string {
	if (text.length <= length) return text
	const cut = text.slice(0, length)
	return /[\uD800-\uDBFF]$/.test(cut) ? cut.slice(0, -1) : cut
}
