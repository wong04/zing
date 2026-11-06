/**
 * What Background remover accepts: a picture the browser can decode (PNG, JPEG, WebP), judged by the file's own first
 * bytes and not by its name, and small enough that decoding it can't take the PC's memory. This runs before anything is
 * decoded.
 */

/**
 * Decoding makes 4 bytes a pixel, and the mask, the result and the PNG that is written add about 9 more, so 36 million
 * pixels (6000 by 6000, which holds a 24-megapixel camera and a 36-megapixel one) is about half a gigabyte at its peak.
 */
export const MAX_PIXELS = 36_000_000
/** How much of a file is read to tell its format and size: a JPEG's size can sit after a long run of tags. */
export const SNIFF_BYTES = 1 << 20

export type ImageFormat = 'png' | 'jpeg' | 'webp'
export interface ImageInfo {
	readonly format: ImageFormat
	readonly width: number
	readonly height: number
}

export class UnsupportedImageError extends Error {
	override name = 'UnsupportedImageError'
}
export class ImageTooLargeError extends Error {
	override name = 'ImageTooLargeError'
}
export class CorruptImageError extends Error {
	override name = 'CorruptImageError'
}

const startsWith = (bytes: Uint8Array, offset: number, text: string): boolean => [...text].every((ch, i) => bytes[offset + i] === ch.charCodeAt(0))
const u32be = (b: Uint8Array, at: number): number => ((b[at]! << 24) | (b[at + 1]! << 16) | (b[at + 2]! << 8) | b[at + 3]!) >>> 0
const u16be = (b: Uint8Array, at: number): number => (b[at]! << 8) | b[at + 1]!
const u16le = (b: Uint8Array, at: number): number => b[at]! | (b[at + 1]! << 8)
const u24le = (b: Uint8Array, at: number): number => b[at]! | (b[at + 1]! << 8) | (b[at + 2]! << 16)

const HEIF_BRANDS = ['heic', 'heix', 'hevc', 'hevx', 'heim', 'heis', 'hevm', 'hevs', 'mif1', 'msf1', 'avif', 'avis']

function unsupported(what: string): UnsupportedImageError {
	return new UnsupportedImageError(`${what} can't be handled by the background remover, which takes PNG, JPEG and WebP. Use Convert to make a PNG or JPEG first, or open it in the viewer`)
}

function cutOff(): CorruptImageError {
	return new CorruptImageError("This file looks damaged or cut short, so it can't be read as a picture")
}

function pngSize(b: Uint8Array): { width: number; height: number } {
	if (b.length < 24 || !startsWith(b, 12, 'IHDR')) throw cutOff()
	return { width: u32be(b, 16), height: u32be(b, 20) }
}

// Start-of-frame markers carry the size; 0xC4, 0xC8 and 0xCC share the range but are not frames.
const isFrame = (marker: number): boolean => marker >= 0xc0 && marker <= 0xcf && marker !== 0xc4 && marker !== 0xc8 && marker !== 0xcc

function jpegSize(b: Uint8Array): { width: number; height: number } {
	let at = 2
	while (at + 4 <= b.length) {
		if (b[at] !== 0xff) throw cutOff()
		const marker = b[at + 1]!
		if (marker === 0xff) {
			at++
			continue
		}
		if (marker === 0xd8 || marker === 0x01 || (marker >= 0xd0 && marker <= 0xd7)) {
			at += 2
			continue
		}
		if (isFrame(marker)) {
			if (at + 9 > b.length) throw cutOff()
			return { width: u16be(b, at + 7), height: u16be(b, at + 5) }
		}
		at += 2 + u16be(b, at + 2)
	}
	throw cutOff()
}

function webpSize(b: Uint8Array): { width: number; height: number } {
	if (b.length < 30) throw cutOff()
	if (startsWith(b, 12, 'VP8X')) return { width: u24le(b, 24) + 1, height: u24le(b, 27) + 1 }
	if (startsWith(b, 12, 'VP8L')) {
		const bits = (b[21]! | (b[22]! << 8) | (b[23]! << 16) | (b[24]! << 24)) >>> 0
		return { width: (bits & 0x3fff) + 1, height: ((bits >>> 14) & 0x3fff) + 1 }
	}
	if (startsWith(b, 12, 'VP8 ')) return { width: u16le(b, 26) & 0x3fff, height: u16le(b, 28) & 0x3fff }
	throw cutOff()
}

/**
 * Reads the format and size from the first bytes of a file.
 * @throws UnsupportedImageError it is not a PNG, JPEG or WebP (HEIC and the rest are named, and Convert is suggested).
 * @throws ImageTooLargeError it has more than {@link MAX_PIXELS} pixels.
 * @throws CorruptImageError it is cut short or has no size.
 */
export function checkImage(bytes: Uint8Array): ImageInfo {
	if (bytes.length === 0) throw cutOff()
	let format: ImageFormat
	let size: { width: number; height: number }
	if (startsWith(bytes, 0, '\x89PNG\r\n\x1a\n')) {
		format = 'png'
		size = pngSize(bytes)
	} else if (bytes[0] === 0xff && bytes[1] === 0xd8) {
		format = 'jpeg'
		size = jpegSize(bytes)
	} else if (startsWith(bytes, 0, 'RIFF') && startsWith(bytes, 8, 'WEBP')) {
		format = 'webp'
		size = webpSize(bytes)
	} else if (startsWith(bytes, 4, 'ftyp') && HEIF_BRANDS.includes(String.fromCharCode(...bytes.slice(8, 12)))) {
		throw unsupported('A HEIC or AVIF picture')
	} else if (startsWith(bytes, 0, 'GIF8')) {
		throw unsupported('A GIF')
	} else {
		throw unsupported('This file')
	}
	if (size.width === 0 || size.height === 0) throw new CorruptImageError("This picture says it has no size, so it can't be read")
	if (size.width * size.height > MAX_PIXELS) {
		const megapixels = Math.round((size.width * size.height) / 1_000_000)
		throw new ImageTooLargeError(`This picture is ${size.width} by ${size.height}, about ${megapixels} megapixels. The background remover takes up to ${MAX_PIXELS / 1_000_000} megapixels, because it needs about half a gigabyte of memory for that many; make it smaller first`)
	}
	return { format, ...size }
}
