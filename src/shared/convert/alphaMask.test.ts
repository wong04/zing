import { describe, expect, it } from 'vitest'
import { applyMask, MaskSizeError, normaliseMask, resizeMask } from './alphaMask'

describe('applyMask', () => {
	it('sets alpha from the mask and keeps the colour', () => {
		const rgba = new Uint8ClampedArray([10, 20, 30, 255, 40, 50, 60, 255, 70, 80, 90, 255])
		const out = applyMask(rgba, new Float32Array([0, 0.5, 1]), 3, 1)
		expect([...out]).toEqual([10, 20, 30, 0, 40, 50, 60, 128, 70, 80, 90, 255])
	})
	it('does not change the input', () => {
		const rgba = new Uint8ClampedArray([1, 2, 3, 255])
		applyMask(rgba, new Float32Array([0]), 1, 1)
		expect([...rgba]).toEqual([1, 2, 3, 255])
	})
	it('keeps a pixel the source already made see-through', () => {
		const out = applyMask(new Uint8ClampedArray([9, 9, 9, 0]), new Float32Array([1]), 1, 1)
		expect(out[3]).toBe(0)
	})
	it('clamps a mask value outside 0 to 1', () => {
		const out = applyMask(new Uint8ClampedArray(8).fill(255), new Float32Array([-0.5, 3]), 2, 1)
		expect([out[3], out[7]]).toEqual([0, 255])
	})
	it('throws when the mask does not fit the image', () => {
		expect(() => applyMask(new Uint8ClampedArray(16), new Float32Array(3), 2, 2)).toThrow(MaskSizeError)
	})
	it('throws when the pixels do not fit the stated size', () => {
		expect(() => applyMask(new Uint8ClampedArray(12), new Float32Array(4), 2, 2)).toThrow(MaskSizeError)
	})
	it('throws on a mask value that is not a number', () => {
		expect(() => applyMask(new Uint8ClampedArray(4), new Float32Array([Number.NaN]), 1, 1)).toThrow(RangeError)
	})
})

describe('normaliseMask', () => {
	it('stretches the lowest value to 0 and the highest to 1', () => {
		const out = normaliseMask(new Float32Array([0.2, 0.4, 0.6]))
		expect(out[0]).toBeCloseTo(0)
		expect(out[1]).toBeCloseTo(0.5)
		expect(out[2]).toBeCloseTo(1)
	})
	it('throws for a flat output, which separates nothing', () => {
		expect(() => normaliseMask(new Float32Array([0.3, 0.3, 0.3]))).toThrow(RangeError)
	})
})

describe('resizeMask', () => {
	it('keeps a mask that is already the right size', () => {
		expect([...resizeMask(new Float32Array([0, 1, 1, 0]), 2, 2, 2, 2)]).toEqual([0, 1, 1, 0])
	})
	it('blends between neighbours when it grows', () => {
		const out = resizeMask(new Float32Array([0, 1]), 2, 1, 4, 1)
		expect(out[0]).toBeCloseTo(0)
		expect(out[3]).toBeCloseTo(1)
		expect(out[1]).toBeGreaterThan(0)
		expect(out[1]).toBeLessThan(out[2]!)
	})
	it('can shrink to one pixel', () => {
		expect(resizeMask(new Float32Array([1, 1, 1, 1]), 2, 2, 1, 1)[0]).toBeCloseTo(1)
	})
	it('throws when the source does not match its size', () => {
		expect(() => resizeMask(new Float32Array(3), 2, 2, 4, 4)).toThrow(MaskSizeError)
	})
})
