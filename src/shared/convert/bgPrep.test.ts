import { describe, expect, it } from 'vitest'
import { MaskSizeError } from './alphaMask'
import { toModelInput, type Normalisation } from './bgPrep'

const plain: Normalisation = { mean: [0.5, 0.5, 0.5], std: [1, 1, 1], divideByMax: false }

describe('toModelInput', () => {
	it('lays the channels out one plane after another', () => {
		// Two pixels: red-ish then blue-ish.
		const input = toModelInput(new Uint8ClampedArray([255, 0, 0, 255, 0, 0, 255, 255]), 2, 1, plain)
		expect(input).toHaveLength(6)
		expect([...input.slice(0, 2)]).toEqual([0.5, -0.5])
		expect([...input.slice(2, 4)]).toEqual([-0.5, -0.5])
		expect([...input.slice(4, 6)]).toEqual([-0.5, 0.5])
	})
	it('divides by the brightest value first when asked, so a dim picture keeps its range', () => {
		const dim = new Uint8ClampedArray([100, 50, 0, 255])
		const out = toModelInput(dim, 1, 1, { mean: [0, 0, 0], std: [1, 1, 1], divideByMax: true })
		expect([...out]).toEqual([1, 0.5, 0])
	})
	it('does not divide a black picture by zero', () => {
		const out = toModelInput(new Uint8ClampedArray([0, 0, 0, 255]), 1, 1, { mean: [0, 0, 0], std: [1, 1, 1], divideByMax: true })
		expect([...out]).toEqual([0, 0, 0])
	})
	it('applies each channel its own mean and std', () => {
		const out = toModelInput(new Uint8ClampedArray([255, 255, 255, 255]), 1, 1, { mean: [0.5, 0.25, 0], std: [0.5, 0.25, 1], divideByMax: false })
		expect([...out]).toEqual([1, 3, 1])
	})
	it('throws when the pixels do not fit the size', () => {
		expect(() => toModelInput(new Uint8ClampedArray(4), 2, 2, plain)).toThrow(MaskSizeError)
	})
})
