import { PdfEditError } from './pdfCore'

/** A picture more than this many pixels is refused before anything decodes it: a small file can describe a huge canvas. */
export const MAX_PICTURE_PIXELS = 100_000_000

export interface PictureSize {
	readonly width: number
	readonly height: number
}

const u16le = (b: Uint8Array, at: number): number => b[at]! | (b[at + 1]! << 8)
const u16be = (b: Uint8Array, at: number): number => (b[at]! << 8) | b[at + 1]!
const u32le = (b: Uint8Array, at: number): number => (b[at]! | (b[at + 1]! << 8) | (b[at + 2]! << 16) | (b[at + 3]! << 24)) >>> 0
const u32be = (b: Uint8Array, at: number): number => ((b[at]! << 24) | (b[at + 1]! << 16) | (b[at + 2]! << 8) | b[at + 3]!) >>> 0
const tag = (b: Uint8Array, at: number, text: string): boolean => [...text].every((c, i) => b[at + i] === c.charCodeAt(0))

const bad = (what: string): PdfEditError => new PdfEditError(`The picture's header is damaged or cut short (${what}).`)

function need(bytes: Uint8Array, length: number, what: string): void {
	if (bytes.length < length) throw bad(what)
}

function png(b: Uint8Array): PictureSize {
	need(b, 24, 'PNG')
	if (!tag(b, 12, 'IHDR')) throw bad('PNG')
	return { width: u32be(b, 16), height: u32be(b, 20) }
}

function jpeg(b: Uint8Array): PictureSize {
	let at = 2
	while (at + 4 <= b.length) {
		if (b[at] !== 0xff) throw bad('JPEG')
		const marker = b[at + 1]!
		if (marker === 0xff) {
			at++
			continue
		}
		const isFrame = marker >= 0xc0 && marker <= 0xcf && marker !== 0xc4 && marker !== 0xc8 && marker !== 0xcc
		if (isFrame) {
			need(b, at + 9, 'JPEG')
			return { height: u16be(b, at + 5), width: u16be(b, at + 7) }
		}
		if (marker === 0xd8 || marker === 0x01 || (marker >= 0xd0 && marker <= 0xd7)) {
			at += 2
			continue
		}
		at += 2 + u16be(b, at + 2)
	}
	throw bad('JPEG')
}

function webp(b: Uint8Array): PictureSize {
	need(b, 25, 'WebP')
	if (!tag(b, 8, 'WEBP')) throw bad('WebP')
	if (tag(b, 12, 'VP8X')) {
		need(b, 30, 'WebP')
		return { width: (b[24]! | (b[25]! << 8) | (b[26]! << 16)) + 1, height: (b[27]! | (b[28]! << 8) | (b[29]! << 16)) + 1 }
	}
	if (tag(b, 12, 'VP8L')) {
		if (b[20] !== 0x2f) throw bad('WebP')
		const bits = u32le(b, 21)
		return { width: (bits & 0x3fff) + 1, height: ((bits >>> 14) & 0x3fff) + 1 }
	}
	if (tag(b, 12, 'VP8 ')) {
		need(b, 30, 'WebP')
		return { width: u16le(b, 26) & 0x3fff, height: u16le(b, 28) & 0x3fff }
	}
	throw bad('WebP')
}

function bmp(b: Uint8Array): PictureSize {
	need(b, 26, 'BMP')
	if (u32le(b, 14) === 12) return { width: u16le(b, 18), height: u16le(b, 20) }
	return { width: Math.abs(u32le(b, 18) | 0), height: Math.abs(u32le(b, 22) | 0) }
}

/**
 * The size a picture says it has, read from its header alone. PNG, JPEG, GIF, WebP and BMP.
 * @throws PdfEditError for a header that is cut short, malformed or of another kind, or a size of zero.
 */
export function pictureSize(bytes: Uint8Array): PictureSize {
	let size: PictureSize
	if (tag(bytes, 1, 'PNG')) size = png(bytes)
	else if (bytes[0] === 0xff && bytes[1] === 0xd8) size = jpeg(bytes)
	else if (tag(bytes, 0, 'GIF8')) {
		need(bytes, 10, 'GIF')
		size = { width: u16le(bytes, 6), height: u16le(bytes, 8) }
	} else if (tag(bytes, 0, 'RIFF')) size = webp(bytes)
	else if (tag(bytes, 0, 'BM')) size = bmp(bytes)
	else throw new PdfEditError("This isn't a PNG, JPEG, GIF, WebP or BMP picture.")
	if (size.width < 1 || size.height < 1) throw bad('size zero')
	return size
}

/** @throws PdfEditError naming the file when the picture is over the limit or its header can't be read. */
export function checkPictureSize(name: string, bytes: Uint8Array): PictureSize {
	let size: PictureSize
	try {
		size = pictureSize(bytes)
	} catch (error) {
		throw new PdfEditError(`${name}: ${error instanceof Error ? error.message : String(error)}`)
	}
	if (size.width * size.height > MAX_PICTURE_PIXELS) {
		throw new PdfEditError(`${name} is ${size.width} by ${size.height} pixels, ${Math.round((size.width * size.height) / 1_000_000)} megapixels; Zing opens pictures up to ${MAX_PICTURE_PIXELS / 1_000_000} megapixels.`)
	}
	return size
}
