// Port of Krackn's Tests/logo: the logo art lands where it should, and the files drawn from it
// (`npm run make-icons`) are complete and current. Krackn parsed its own SVG path data, so half its
// cases test that parser; Zing's logo is plain circles and clip polygons, so those have no counterpart.
import { createHash } from 'node:crypto'
import { readFileSync } from 'node:fs'
import { join } from 'node:path'
import { inflateSync } from 'node:zlib'
import { describe, expect, it } from 'vitest'

const root = join(import.meta.dirname, '..', '..', '..')
const read = (rel: string) => readFileSync(join(root, rel))
const FROSTED = 'assets/brand/zing.svg'
const DARK = 'assets/brand/zing-dark-tile.svg'
const SOURCES = { 'build/icons': FROSTED, 'build/icons-dark-tile': DARK }
const PNG_SIZES = [16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 128, 256, 512, 1024]
const ICO_SIZES = [16, 20, 24, 32, 40, 48, 64, 256]

type Point = [number, number]
interface Half { cx: number; cy: number; r: number; shift: Point; clip: Point[] }
interface Art {
	viewBox: number[]
	tile: { x: number; y: number; w: number; h: number }
	halves: [Half, Half]
	slice: string
}

function at<T>(xs: ArrayLike<T>, i: number): T {
	const value = xs[i]
	if (value === undefined) throw new Error(`nothing at index ${i}`)
	return value
}

/** The first capture group of `re` in `s`. */
function capture(s: string, re: RegExp): string {
	const value = s.match(re)?.[1]
	if (value === undefined) throw new Error(`${re} not found`)
	return value
}

const attr = (tag: string, name: string) => capture(tag, new RegExp(`\\s${name}="([^"]*)"`))

function parse(svg: string): Art {
	const rootTag = capture(svg, /(<svg[^>]*>)/)
	const tileTag = capture(svg, /(<rect[^>]*>)/)
	const halves = [...svg.matchAll(/<g transform="translate\(([-\d.]+) ([-\d.]+)\)">(<circle[^>]*>)<\/g>/g)].map((m): Half => {
		const circle = at(m, 3)
		const clipId = capture(attr(circle, 'clip-path'), /#([^)]+)/)
		const polygon = capture(svg, new RegExp(`<clipPath id="${clipId}"><polygon points="([^"]+)"`))
		return {
			cx: Number(attr(circle, 'cx')),
			cy: Number(attr(circle, 'cy')),
			r: Number(attr(circle, 'r')),
			shift: [Number(at(m, 1)), Number(at(m, 2))],
			clip: polygon.trim().split(/\s+/).map((p) => p.split(',').map(Number) as Point),
		}
	})
	if (halves.length !== 2) throw new Error(`expected two slice halves, found ${halves.length}`)
	return {
		viewBox: attr(rootTag, 'viewBox').split(/\s+/).map(Number),
		tile: { x: Number(attr(tileTag, 'x')), y: Number(attr(tileTag, 'y')), w: Number(attr(tileTag, 'width')), h: Number(attr(tileTag, 'height')) },
		halves: [at(halves, 0), at(halves, 1)],
		// Everything but the tile: gradients a/b, the clips and the two halves.
		slice: [...svg.matchAll(/<linearGradient id="[ab]"[\s\S]*?<\/linearGradient>|<clipPath[\s\S]*?<\/clipPath>|<g transform[\s\S]*?<\/g>/g)].map((m) => m[0]).join(''),
	}
}

/** x + y along a clip edge; the cut runs from top-right to bottom-left, so it is constant there. */
function cutLine(clip: Point[]): number {
	const [a, b] = [at(clip, 0), at(clip, 1)]
	expect(a[0] + a[1]).toBeCloseTo(b[0] + b[1], 1)
	return a[0] + a[1]
}

for (const [out, src] of Object.entries(SOURCES)) {
	describe(`logo art: ${src}`, () => {
		const svg = read(src).toString('utf8')
		const art = parse(svg)
		const [vw, vh] = [at(art.viewBox, 2), at(art.viewBox, 3)]

		it('is a square 1024 viewBox with a size the generator can override', () => {
			expect(art.viewBox).toEqual([0, 0, 1024, 1024])
			expect(svg).toMatch(/<svg[^>]* width="1024" height="1024"/)
		})

		it('has a tile that fills most of the canvas without leaving it', () => {
			const { x, y, w, h } = art.tile
			expect(w).toBe(h)
			expect(w).toBeGreaterThan(vw * 0.85)
			expect(x).toBeGreaterThanOrEqual(0)
			expect(y).toBeGreaterThanOrEqual(0)
			expect(x + w).toBeLessThanOrEqual(vw)
			expect(y + h).toBeLessThanOrEqual(vh)
			expect(x + w / 2).toBeCloseTo(vw / 2)
		})

		it('has two halves of one disc, centred', () => {
			const [a, b] = art.halves
			expect([b.cx, b.cy, b.r]).toEqual([a.cx, a.cy, a.r])
			expect([a.cx, a.cy]).toEqual([vw / 2, vh / 2])
		})

		it('has a disc that spans most of the tile and stays inside it', () => {
			const { x, y, w } = art.tile
			for (const half of art.halves) {
				expect(half.r * 2).toBeGreaterThan(w * 0.6)
				expect(half.cx - half.r + half.shift[0]).toBeGreaterThan(x)
				expect(half.cy - half.r + half.shift[1]).toBeGreaterThan(y)
				expect(half.cx + half.r + half.shift[0]).toBeLessThan(x + w)
				expect(half.cy + half.r + half.shift[1]).toBeLessThan(y + w)
			}
		})

		it('cuts the disc diagonally through its centre, leaving a gap', () => {
			const [a, b] = art.halves
			const [upper, lower] = [cutLine(a.clip), cutLine(b.clip)]
			expect((upper + lower) / 2).toBeCloseTo(a.cx + a.cy, 0)
			const gap = (lower - upper) / Math.SQRT2
			expect(gap).toBeGreaterThan(art.tile.w * 0.05)
			expect(gap).toBeLessThan(art.tile.w * 0.2)
		})

		it('slides the halves apart along the cut', () => {
			const [a, b] = art.halves.map((h) => h.shift) as [Point, Point]
			expect(a[0] + b[0]).toBeCloseTo(0)
			expect(a[1] + b[1]).toBeCloseTo(0)
			expect(a[0] + a[1]).toBeCloseTo(0)
			expect(Math.hypot(a[0], a[1])).toBeGreaterThan(0)
		})

		it(`is the art in ${out} (run npm run make-icons after changing it)`, () => {
			const manifest = JSON.parse(read('build/brand.json').toString('utf8'))
			expect(manifest[src]).toBe(createHash('sha256').update(svg.replace(/\r\n/g, '\n')).digest('hex'))
		})
	})
}

describe('the frosted and dark tiles', () => {
	it('carry the same slice', () => {
		const frosted = parse(read(FROSTED).toString('utf8')).slice
		expect(frosted.length).toBeGreaterThan(0)
		expect(parse(read(DARK).toString('utf8')).slice).toBe(frosted)
	})
})

interface Png { width: number; height: number; rgba: Uint8Array }

/** Width and height from the header of an 8-bit RGBA, non-interlaced PNG, the kind the generator writes. */
function pngSize(buf: Buffer): [number, number] {
	expect(buf.subarray(0, 8)).toEqual(Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]))
	expect(buf.toString('ascii', 12, 16)).toBe('IHDR')
	expect([buf[24], buf[25], buf[28]]).toEqual([8, 6, 0])
	return [buf.readUInt32BE(16), buf.readUInt32BE(20)]
}

function decodePng(buf: Buffer): Png {
	const [width, height] = pngSize(buf)
	const idat: Buffer[] = []
	for (let o = 8; o < buf.length; ) {
		const len = buf.readUInt32BE(o)
		if (buf.toString('ascii', o + 4, o + 8) === 'IDAT') idat.push(buf.subarray(o + 8, o + 8 + len))
		o += 12 + len
	}
	const raw = inflateSync(Buffer.concat(idat))
	const stride = width * 4
	const rgba = new Uint8Array(stride * height)
	for (let y = 0; y < height; y++) {
		const filter = at(raw, y * (stride + 1))
		const line = raw.subarray(y * (stride + 1) + 1, (y + 1) * (stride + 1))
		for (let x = 0; x < stride; x++) {
			const left = x >= 4 ? at(rgba, y * stride + x - 4) : 0
			const up = y > 0 ? at(rgba, (y - 1) * stride + x) : 0
			const upLeft = x >= 4 && y > 0 ? at(rgba, (y - 1) * stride + x - 4) : 0
			const p = left + up - upLeft
			const paeth = Math.abs(p - left) <= Math.abs(p - up) && Math.abs(p - left) <= Math.abs(p - upLeft) ? left : Math.abs(p - up) <= Math.abs(p - upLeft) ? up : upLeft
			const predict = at([0, left, up, (left + up) >> 1, paeth], filter)
			rgba[y * stride + x] = (at(line, x) + predict) & 0xff
		}
	}
	return { width, height, rgba }
}

function pixel(png: Png, x: number, y: number) {
	const o = (y * png.width + x) * 4
	return { r: at(png.rgba, o), g: at(png.rgba, o + 1), b: at(png.rgba, o + 2), a: at(png.rgba, o + 3) }
}

for (const out of Object.keys(SOURCES)) {
	describe(`generated icons: ${out}`, () => {
		it('has every PNG size', () => {
			for (const size of PNG_SIZES) expect(pngSize(read(`${out}/${size}.png`))).toEqual([size, size])
		})

		it('has a .ico with 32-bit PNG images from 16 to 256 px', () => {
			const ico = read(`${out}/icon.ico`)
			expect([ico.readUInt16LE(0), ico.readUInt16LE(2)]).toEqual([0, 1])
			const sizes: number[] = []
			for (let i = 0; i < ico.readUInt16LE(4); i++) {
				const e = 6 + i * 16
				const size = at(ico, e) || 256
				expect(ico.readUInt16LE(e + 6)).toBe(32)
				const png = ico.subarray(ico.readUInt32LE(e + 12), ico.readUInt32LE(e + 12) + ico.readUInt32LE(e + 8))
				expect(pngSize(png)).toEqual([size, size])
				sizes.push(size)
			}
			expect(sizes).toEqual(ICO_SIZES)
		})

		it('keeps both halves and a see-through corner at 16 px', () => {
			const png = decodePng(read(`${out}/16.png`))
			let warm = 0, cool = 0
			for (let y = 0; y < 16; y++) {
				for (let x = 0; x < 16; x++) {
					const { r, b, a } = pixel(png, x, y)
					if (a < 200) continue
					if (r > b + 60) warm++
					if (b > r + 60) cool++
				}
			}
			expect(warm).toBeGreaterThanOrEqual(6)
			expect(cool).toBeGreaterThanOrEqual(6)
			expect(pixel(png, 0, 0).a).toBeLessThan(40)
			expect(pixel(png, 8, 8).a).toBe(255)
		})
	})
}

describe('installer art for Velopack', () => {
	it('has an opaque splash image', () => {
		const png = decodePng(read('build/installer/splash.png'))
		expect([png.width, png.height]).toEqual([480, 300])
		for (const [x, y] of [[0, 0], [479, 0], [0, 299], [479, 299]] as const) expect(pixel(png, x, y).a).toBe(255)
	})

	for (const [name, w, h] of [['msi-top-banner.bmp', 493, 58], ['msi-dialog-background.bmp', 493, 312]] as const) {
		it(`has ${name} at the ${w}x${h} the MSI dialogs need`, () => {
			const bmp = read(`build/installer/${name}`)
			expect(bmp.toString('ascii', 0, 2)).toBe('BM')
			expect(bmp.readUInt32LE(2)).toBe(bmp.length)
			expect([bmp.readInt32LE(18), bmp.readInt32LE(22), bmp.readUInt16LE(28)]).toEqual([w, h, 24])
			expect(bmp.length).toBe(54 + Math.ceil((w * 3) / 4) * 4 * h)
		})
	}
})
