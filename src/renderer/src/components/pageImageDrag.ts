// An image dragged out of a web page reaches a tool's drop zone as two strings, never a file: `text/uri-list` (the image's
// address, or the link's when the image is inside one) and `text/html` (the image's own markup, with an absolute src). The host
// fetches the address (`files:open-dropped-image`) and judges what comes back.

const URI_LIST = 'text/uri-list'
const HTML = 'text/html'

interface IncomingTransfer {
	readonly types: readonly string[]
	getData(type: string): string
}

/** Whether a drag offers an address a picture could be fetched from. */
export function isPageImageDrag(types: readonly string[]): boolean {
	return types.includes(URI_LIST)
}

const IMG_SRC = /<img\b[^>]*?\ssrc\s*=\s*(?:"([^"]*)"|'([^']*)'|([^\s>]+))/i
const ENTITIES: Readonly<Record<string, string>> = { '&amp;': '&', '&quot;': '"', '&#39;': "'", '&lt;': '<', '&gt;': '>' }

/** The dragged image's address: the `<img>`'s own src first (a linked image's uri-list is the link), else the first address in the uri-list. */
export function pageImageAddress(transfer: IncomingTransfer): string | undefined {
	const markup = transfer.types.includes(HTML) ? IMG_SRC.exec(transfer.getData(HTML)) : null
	const src = markup ? (markup[1] ?? markup[2] ?? markup[3])! : undefined
	if (src) return src.replace(/&(amp|quot|#39|lt|gt);/g, (entity) => ENTITIES[entity]!)
	if (!transfer.types.includes(URI_LIST)) return undefined
	// RFC 2483: one address per line, and a line starting with # is a comment.
	return transfer
		.getData(URI_LIST)
		.split(/\r?\n/)
		.map((line) => line.trim())
		.find((line) => line !== '' && !line.startsWith('#'))
}
