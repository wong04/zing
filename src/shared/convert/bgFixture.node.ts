import { deflateSync } from 'node:zlib'

export interface Fixture {
	readonly width: number
	readonly height: number
	readonly rgba: Uint8ClampedArray
	/** 1 where the drawn subject is, 0 on the plain background. */
	readonly truth: Uint8Array
}

/**
 * A picture the background remover can be checked on: a plain pale wall and floor, and one shaded orange with a green
 * stalk and dark speckles standing on it. The subject's pixels are known, so a mask can be scored against them.
 */
export function drawFixture(width: number, height: number): Fixture {
	const rgba = new Uint8ClampedArray(width * height * 4)
	const truth = new Uint8Array(width * height)
	const cx = width / 2
	const cy = height * 0.55
	const rx = width * 0.27
	const ry = height * 0.33
	for (let y = 0; y < height; y++) {
		for (let x = 0; x < width; x++) {
			const at = y * width + x
			let r = 232
			let g = 230
			let b = 224
			if (y > height * 0.8) {
				r = 205
				g = 198
				b = 188
			}
			const dx = (x - cx) / rx
			const dy = (y - cy) / ry
			const inside = dx * dx + dy * dy <= 1
			const stalk = Math.abs(x - cx) < width * 0.012 && y > cy - ry - height * 0.08 && y <= cy - ry + 2
			if (inside) {
				const light = 1 - 0.45 * Math.max(0, (dx + dy) / 2 + 0.3)
				const speckle = (Math.sin(x * 0.9) * Math.cos(y * 0.7) > 0.93 ? 0.55 : 1)
				r = 235 * light * speckle
				g = 120 * light * speckle
				b = 25 * light * speckle
			} else if (stalk) {
				r = 60
				g = 110
				b = 40
			}
			rgba[at * 4] = r
			rgba[at * 4 + 1] = g
			rgba[at * 4 + 2] = b
			rgba[at * 4 + 3] = 255
			truth[at] = inside || stalk ? 1 : 0
		}
	}
	return { width, height, rgba, truth }
}

const CRC_TABLE = Array.from({ length: 256 }, (_, n) => {
	let c = n
	for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1
	return c >>> 0
})

function crc32(bytes: Uint8Array): number {
	let c = 0xffffffff
	for (const byte of bytes) c = CRC_TABLE[(c ^ byte) & 255]! ^ (c >>> 8)
	return (c ^ 0xffffffff) >>> 0
}

function chunk(type: string, data: Uint8Array): Buffer {
	const body = Buffer.concat([Buffer.from(type, 'ascii'), data])
	const out = Buffer.alloc(8 + data.length + 4)
	out.writeUInt32BE(data.length, 0)
	body.copy(out, 4)
	out.writeUInt32BE(crc32(body), 8 + data.length)
	return out
}

/** A PNG (8-bit RGBA) of the pixels. */
export function encodePng(rgba: Uint8ClampedArray, width: number, height: number): Buffer {
	const header = Buffer.alloc(13)
	header.writeUInt32BE(width, 0)
	header.writeUInt32BE(height, 4)
	header.set([8, 6, 0, 0, 0], 8)
	const rows = Buffer.alloc((width * 4 + 1) * height)
	for (let y = 0; y < height; y++) {
		const row = y * (width * 4 + 1)
		rows[row] = 0
		rows.set(rgba.subarray(y * width * 4, (y + 1) * width * 4), row + 1)
	}
	return Buffer.concat([Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]), chunk('IHDR', header), chunk('IDAT', deflateSync(rows)), chunk('IEND', Buffer.alloc(0))])
}

/** How well a 0-to-1 mask matches the drawn subject: the mean mask value on the subject and on the background. */
export function scoreMask(mask: Float32Array, truth: Uint8Array): { inside: number; outside: number } {
	let inside = 0
	let outside = 0
	let insideCount = 0
	for (let i = 0; i < truth.length; i++) {
		if (truth[i]) {
			inside += mask[i]!
			insideCount++
		} else outside += mask[i]!
	}
	return { inside: inside / insideCount, outside: outside / (truth.length - insideCount) }
}
