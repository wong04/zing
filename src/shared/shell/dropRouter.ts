// Port of the pure parts of Krackn App/DropRouter.swift, plus
// `PanelContent.acceptsFiles` from App/Sidebar.swift.
//
// Turns anything dropped on the app into a local file. A drag from Explorer
// carries a file, but a drag out of a web page usually doesn't: it may be an
// http(s) URL, raw image bytes, or just some other representation. Each of
// those has to be resolved — and for a web URL that means downloading it —
// before the viewer or converter has something to work with. This module
// decides *what* to do; the main process does the reading, writing and
// downloading.
import { Section, UtilityTool, type PanelContent } from '../toolbox/panelContent'

/** What one dragged item offers, already read off the drag. */
export type DropPayload = {
	/** A file location: an OS path or a `file:` URL. */
	readonly fileLocation?: string
	/** Raw image bytes — what a browser supplies when you drag an <img>. */
	readonly image?: Uint8Array
	/** A URL offered as a URL (text/uri-list), not as plain text. */
	readonly url?: string
	readonly suggestedName?: string
	/** MIME types of any other representations on offer, in the order offered. */
	readonly otherTypes?: readonly string[]
}

export type DropPlan =
	/** A real file on disk: use it where it is. */
	| { readonly kind: 'open'; readonly path: string }
	/** Bytes to land in the inbox under this name. */
	| { readonly kind: 'write'; readonly bytes: Uint8Array; readonly name: string }
	/** A web URL to download into the inbox (name it with `nameForDownload` once the response is in). */
	| { readonly kind: 'download'; readonly url: string }
	/** Ask the source for a file in each of these types, in order, until one arrives. */
	| { readonly kind: 'materialise'; readonly types: readonly string[] }

/**
 * Where dropped web content lands, under the user's Downloads folder, rather
 * than a temp directory: a picture dragged out of a web page is a file the
 * user now owns, and burying it in a temp folder meant they could see it in
 * the app but never find it again on disk — and the system would eventually
 * delete it.
 */
export const inboxFolderName = 'Zing'

/**
 * Drag types a file drop target accepts: Krackn's [fileURL, image, url, text]
 * in DataTransfer terms (a dragged image arrives as a File).
 *
 * Never a wildcard. Krackn learned that registering the root type made every
 * view a universal drop target and swallowed right-clicks underneath it.
 */
export const acceptedDragTypes: readonly string[] = ['Files', 'text/uri-list', 'text/plain']

/**
 * For views that only want to *notice* a drag passing over them and never
 * consume it — the tab strip's spring-loading.
 */
export const hoverDragTypes: readonly string[] = ['Files', 'text/uri-list']

const fallbackImageExtension = 'png'
const fallbackDownloadExtension = 'bin'
const fallbackDownloadName = 'download'

/** `UTType(mimeType:).preferredFilenameExtension` for the types that actually turn up in drags. */
const extensionForMimeType: Readonly<Record<string, string>> = {
	'image/png': 'png',
	'image/jpeg': 'jpeg',
	'image/gif': 'gif',
	'image/webp': 'webp',
	'image/avif': 'avif',
	'image/heic': 'heic',
	'image/bmp': 'bmp',
	'image/tiff': 'tiff',
	'image/svg+xml': 'svg',
	'image/x-icon': 'ico',
	'image/vnd.microsoft.icon': 'ico',
	'application/pdf': 'pdf',
	'application/zip': 'zip',
	'application/json': 'json',
	'text/plain': 'txt',
	'text/html': 'html',
	'text/csv': 'csv',
	'video/mp4': 'mp4',
	'video/webm': 'webm',
	'video/quicktime': 'mov',
	'audio/mpeg': 'mp3',
	'audio/wav': 'wav',
	'audio/flac': 'flac',
}

/** Which features do something with a dropped file. */
export function acceptsFiles(content: PanelContent): boolean {
	if (content.kind === 'section') return content.section === Section.viewer || content.section === Section.convert
	return content.tool === UtilityTool.bgRemove
}

export function acceptsDrag(types: readonly string[]): boolean {
	return types.some((type) => acceptedDragTypes.includes(type))
}

export function noticesDrag(types: readonly string[]): boolean {
	return types.some((type) => hoverDragTypes.includes(type))
}

/** What to do with one dropped item, cheapest first. Null when there's nothing file-like in it. */
export function planDrop(payload: DropPayload, now: Date): DropPlan | null {
	// 1. A real file on disk — the Explorer case, and the cheapest.
	const path = payload.fileLocation === undefined ? null : asFilePath(payload.fileLocation)
	if (path !== null) return { kind: 'open', path }

	// 2. Raw image bytes.
	if (payload.image && payload.image.length > 0) {
		return { kind: 'write', bytes: payload.image, name: nameForDroppedImage(payload.image, payload.suggestedName, now) }
	}

	// 3. A web URL — download it. The common case for dragging an image out
	//    of a page that only offers a link.
	const url = payload.url === undefined ? null : asWebURL(payload.url)
	if (url !== null) return { kind: 'download', url }

	// 4. Anything else with a file behind it: meshes, video, archives and
	//    unrecognised types from an app that doesn't offer a file location.
	//
	//    Text and URL types are excluded deliberately. A dragged *string* can
	//    be materialised into a temp file, so without this guard dropping a
	//    text selection onto a tool would silently manufacture a junk file in
	//    Downloads. Anything that's genuinely a text file arrives as a file
	//    location and is caught by step 1 long before this.
	const types = (payload.otherTypes ?? []).filter(isMaterialisable)
	if (types.length > 0) return { kind: 'materialise', types }
	return null
}

/** An OS path for a dropped file location: a Windows path as given, or a `file:` URL converted. Null for anything else. */
export function asFilePath(location: string): string | null {
	if (/^[A-Za-z]:[\\/]/.test(location) || location.startsWith('\\\\')) return location
	if (!URL.canParse(location)) return null
	const url = new URL(location)
	if (url.protocol !== 'file:') return null
	const pathname = decodePercent(url.pathname)
	if (url.host !== '') return `\\\\${url.host}${pathname.replaceAll('/', '\\')}`
	return pathname.replace(/^\/(?=[A-Za-z]:)/, '').replaceAll('/', '\\')
}

/** The URL if it's http(s), else null. Surrounding whitespace is tolerated. */
export function asWebURL(item: string): string | null {
	const text = item.trim()
	if (!URL.canParse(text)) return null
	const url = new URL(text)
	if (url.protocol !== 'http:' && url.protocol !== 'https:') return null
	return url.href
}

/** Name for dropped image bytes. Without an extension, the real type is sniffed from the bytes rather than trusting the name. */
export function nameForDroppedImage(bytes: Uint8Array, suggestedName: string | undefined, now: Date): string {
	const name = safeFileName(suggestedName ?? `dropped-${Math.trunc(now.getTime() / 1000)}`)
	if (pathExtension(name) !== '') return name
	return `${name}.${detectExtension(bytes) ?? fallbackImageExtension}`
}

/** Name for a downloaded drop: the URL's last path component, given an extension from the response type or the bytes if it has none. */
export function nameForDownload(url: string, mimeType: string | null, bytes: Uint8Array): string {
	const name = safeFileName(lastPathComponent(url)) || fallbackDownloadName
	if (pathExtension(name) !== '') return name
	const fromType = mimeType === null ? undefined : extensionForMimeType[mimeType.toLowerCase()]
	return `${name}.${fromType ?? detectExtension(bytes) ?? fallbackDownloadExtension}`
}

/** Magic-number sniffing for the formats that actually turn up in drags. */
export function detectExtension(bytes: Uint8Array): string | null {
	if (bytes.length < 4) return null
	if (startsWith(bytes, [0x89, 0x50, 0x4e, 0x47])) return 'png'
	if (startsWith(bytes, [0xff, 0xd8, 0xff])) return 'jpg'
	if (startsWith(bytes, [0x47, 0x49, 0x46])) return 'gif'
	if (startsWith(bytes, [0x25, 0x50, 0x44, 0x46])) return 'pdf'
	if (bytes.length >= 12 && startsWith(bytes, [0x52, 0x49, 0x46, 0x46]) && startsWith(bytes.subarray(8), [0x57, 0x45, 0x42, 0x50])) {
		return 'webp'
	}
	if (bytes.length >= 8 && startsWith(bytes.subarray(4), [0x66, 0x74, 0x79, 0x70])) return 'mp4'
	return null
}

/** `name`, or "name 2.ext", "name 3.ext"… — the first that `exists` says is free. */
export function uniqueName(name: string, exists: (candidate: string) => boolean): string {
	const ext = pathExtension(name)
	const base = ext === '' ? name : name.slice(0, -(ext.length + 1))
	let candidate = name
	for (let counter = 2; exists(candidate); counter++) {
		candidate = ext === '' ? `${base} ${counter}` : `${base} ${counter}.${ext}`
	}
	return candidate
}

/**
 * A name Windows will accept: the characters it reserves become "-", and the
 * trailing dots and spaces it silently strips are removed up front.
 */
export function safeFileName(name: string): string {
	// eslint-disable-next-line no-control-regex -- control characters are exactly what Windows refuses
	return name.replace(/[<>:"/\\|?*\x00-\x1f]/g, '-').replace(/[. ]+$/, '')
}

/** Foundation's `pathExtension`: after the last dot, but a leading dot ("…/.hidden") isn't one. */
export function pathExtension(name: string): string {
	const dot = name.lastIndexOf('.')
	if (dot <= 0 || dot === name.length - 1) return ''
	return name.slice(dot + 1)
}

function lastPathComponent(url: string): string {
	const segments = new URL(url).pathname.split('/').filter((s) => s !== '')
	const last = segments[segments.length - 1] ?? ''
	return decodePercent(last)
}

function decodePercent(text: string): string {
	try {
		return decodeURIComponent(text)
	} catch (error) {
		// Not valid percent-encoding ("100%.png"): the text is already literal.
		if (error instanceof URIError) return text
		throw error
	}
}

/** Text and URL representations (text/plain, text/uri-list, text/x-moz-url…) are all text/*. */
function isMaterialisable(type: string): boolean {
	return !type.toLowerCase().startsWith('text/')
}

function startsWith(bytes: Uint8Array, prefix: readonly number[]): boolean {
	return prefix.every((byte, i) => bytes[i] === byte)
}
