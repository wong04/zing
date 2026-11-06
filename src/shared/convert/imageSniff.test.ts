import { describe, expect, it } from 'vitest'
import { checkImage, MAX_PIXELS, UnsupportedImageError, ImageTooLargeError, CorruptImageError } from './imageSniff'

const ascii = (text: string): number[] => [...text].map((ch) => ch.charCodeAt(0))
const u32be = (n: number): number[] => [(n >>> 24) & 255, (n >>> 16) & 255, (n >>> 8) & 255, n & 255]
const u16be = (n: number): number[] => [(n >>> 8) & 255, n & 255]
const u24le = (n: number): number[] => [n & 255, (n >>> 8) & 255, (n >>> 16) & 255]
const u32le = (n: number): number[] => [n & 255, (n >>> 8) & 255, (n >>> 16) & 255, (n >>> 24) & 255]

const png = (w: number, h: number): Uint8Array => new Uint8Array([0x89, ...ascii('PNG\r\n'), 0x1a, 0x0a, ...u32be(13), ...ascii('IHDR'), ...u32be(w), ...u32be(h), 8, 6, 0, 0, 0])
const jpeg = (w: number, h: number): Uint8Array =>
	new Uint8Array([0xff, 0xd8, 0xff, 0xe0, ...u16be(16), ...ascii('JFIF'), 0, 1, 1, 0, 0, 1, 0, 1, 0, 0, 0xff, 0xc0, ...u16be(11), 8, ...u16be(h), ...u16be(w), 1, 1, 0x11, 0])
const webpLossy = (w: number, h: number): Uint8Array =>
	new Uint8Array([...ascii('RIFF'), ...u32le(30), ...ascii('WEBPVP8 '), ...u32le(10), 0, 0, 0, 0x9d, 0x01, 0x2a, w & 255, w >> 8, h & 255, h >> 8])
const webpExtended = (w: number, h: number): Uint8Array =>
	new Uint8Array([...ascii('RIFF'), ...u32le(30), ...ascii('WEBPVP8X'), ...u32le(10), 0, 0, 0, 0, ...u24le(w - 1), ...u24le(h - 1)])
const webpLossless = (w: number, h: number): Uint8Array => {
	const bits = (w - 1) | ((h - 1) << 14)
	return new Uint8Array([...ascii('RIFF'), ...u32le(30), ...ascii('WEBPVP8L'), ...u32le(10), 0x2f, ...u32le(bits), 0, 0, 0, 0, 0])
}
const heic = new Uint8Array([...u32be(24), ...ascii('ftypheic'), 0, 0, 0, 0, ...ascii('mif1heic')])

describe('checkImage', () => {
	it('reads the size of a PNG, a JPEG and the three kinds of WebP', () => {
		expect(checkImage(png(640, 480))).toEqual({ format: 'png', width: 640, height: 480 })
		expect(checkImage(jpeg(300, 200))).toEqual({ format: 'jpeg', width: 300, height: 200 })
		expect(checkImage(webpLossy(321, 123))).toEqual({ format: 'webp', width: 321, height: 123 })
		expect(checkImage(webpExtended(4000, 3000))).toEqual({ format: 'webp', width: 4000, height: 3000 })
		expect(checkImage(webpLossless(777, 555))).toEqual({ format: 'webp', width: 777, height: 555 })
	})
	it('says HEIC is not handled here and points to Convert and the viewer', () => {
		expect(() => checkImage(heic)).toThrow(UnsupportedImageError)
		expect(() => checkImage(heic)).toThrow(/Convert/)
	})
	it('refuses other formats by name, not by their file name', () => {
		expect(() => checkImage(new Uint8Array([...ascii('GIF89a'), 1, 0, 1, 0]))).toThrow(UnsupportedImageError)
		expect(() => checkImage(new Uint8Array(ascii('<svg xmlns="http://www.w3.org/2000/svg"/>')))).toThrow(UnsupportedImageError)
		expect(() => checkImage(new Uint8Array(ascii('hello world, not a picture')))).toThrow(UnsupportedImageError)
	})
	it('refuses an image over the pixel limit before anything is decoded', () => {
		expect(() => checkImage(png(MAX_PIXELS, 2))).toThrow(ImageTooLargeError)
		expect(() => checkImage(png(MAX_PIXELS, 2))).toThrow(/megapixels/)
		expect(checkImage(png(6000, 6000)).width).toBe(6000)
	})
	it('refuses a picture with no size', () => {
		expect(() => checkImage(png(0, 10))).toThrow(CorruptImageError)
	})
	it('says a cut-off file is damaged', () => {
		expect(() => checkImage(png(10, 10).slice(0, 14))).toThrow(CorruptImageError)
		expect(() => checkImage(jpeg(10, 10).slice(0, 12))).toThrow(CorruptImageError)
		expect(() => checkImage(new Uint8Array(0))).toThrow(CorruptImageError)
	})
})
