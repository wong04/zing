import { describe, expect, it } from 'vitest'
import { PdfEditError } from './pdfCore'
import { MAX_PICTURE_PIXELS, checkPictureSize, pictureSize } from './pictureHeader'

const u32be = (n: number): number[] => [(n >>> 24) & 255, (n >>> 16) & 255, (n >>> 8) & 255, n & 255]
const u32le = (n: number): number[] => [n & 255, (n >>> 8) & 255, (n >>> 16) & 255, (n >>> 24) & 255]
const u16le = (n: number): number[] => [n & 255, (n >>> 8) & 255]
const ascii = (t: string): number[] => [...t].map((c) => c.charCodeAt(0))

const png = (w: number, h: number): Uint8Array => Uint8Array.from([0x89, ...ascii('PNG'), 0x0d, 0x0a, 0x1a, 0x0a, ...u32be(13), ...ascii('IHDR'), ...u32be(w), ...u32be(h), 8, 6, 0, 0, 0])
const jpeg = (w: number, h: number): Uint8Array =>
	Uint8Array.from([0xff, 0xd8, 0xff, 0xe0, 0, 4, 0, 0, 0xff, 0xc4, 0, 3, 0, 0xff, 0xc2, 0, 11, 8, (h >> 8) & 255, h & 255, (w >> 8) & 255, w & 255, 1, 1, 0x11, 0])
const gif = (w: number, h: number): Uint8Array => Uint8Array.from([...ascii('GIF89a'), ...u16le(w), ...u16le(h), 0, 0, 0])
const bmp = (w: number, h: number): Uint8Array => Uint8Array.from([...ascii('BM'), ...u32le(0), ...u32le(0), ...u32le(54), ...u32le(40), ...u32le(w), ...u32le(h >>> 0), ...u16le(1), ...u16le(24)])
const riff = (chunk: number[]): Uint8Array => Uint8Array.from([...ascii('RIFF'), ...u32le(chunk.length + 4), ...ascii('WEBP'), ...chunk])
const webpLossy = (w: number, h: number): Uint8Array => riff([...ascii('VP8 '), ...u32le(10), 0, 0, 0, 0x9d, 0x01, 0x2a, ...u16le(w), ...u16le(h)])
const webpLossless = (w: number, h: number): Uint8Array => riff([...ascii('VP8L'), ...u32le(5), 0x2f, ...u32le((w - 1) | ((h - 1) << 14))])
const webpExtended = (w: number, h: number): Uint8Array => riff([...ascii('VP8X'), ...u32le(10), 0, 0, 0, 0, (w - 1) & 255, ((w - 1) >> 8) & 255, ((w - 1) >> 16) & 255, (h - 1) & 255, ((h - 1) >> 8) & 255, ((h - 1) >> 16) & 255])

describe('picture sizes are read from the header', () => {
	it.each([
		['png', png(640, 480)],
		['jpeg', jpeg(640, 480)],
		['webp lossy', webpLossy(640, 480)],
		['webp lossless', webpLossless(640, 480)],
		['webp extended', webpExtended(640, 480)],
	])('%s', (_name, bytes) => expect(pictureSize(bytes)).toEqual({ width: 640, height: 480 }))

	it('does not read a GIF or a BMP: a session never hands those over', () => {
		for (const other of [gif(1, 1), bmp(1, 1)]) expect(() => pictureSize(other)).toThrow(/PNG, JPEG or WebP/)
	})

	it('refuses a header that is cut short or makes no sense', () => {
		for (const bad of [new Uint8Array(), png(1, 1).slice(0, 20), jpeg(1, 1).slice(0, 12), riff([]), Uint8Array.from(ascii('not a picture at all')), png(0, 5)]) {
			expect(() => pictureSize(bad)).toThrow(PdfEditError)
		}
	})
	it('refuses a picture over the megapixel limit before anything decodes it', () => {
		expect(MAX_PICTURE_PIXELS).toBe(100_000_000)
		expect(() => checkPictureSize('bomb.png', png(20000, 20000))).toThrow(/bomb\.png.*megapixels/)
		expect(() => checkPictureSize('ok.png', png(10000, 10000))).not.toThrow()
		expect(() => checkPictureSize('bomb.jpg', jpeg(65535, 65535))).toThrow(PdfEditError)
	})
})
