import { describe, expect, it } from 'vitest'
import { isDarkIcon } from './darkIcon'

type Pixel = readonly [number, number, number, number]

const image = (...runs: readonly (readonly [Pixel, number])[]): Uint8ClampedArray => new Uint8ClampedArray(runs.flatMap(([pixel, count]) => Array.from({ length: count }, () => pixel).flat()))

const black: Pixel = [31, 35, 40, 255]
const white: Pixel = [255, 255, 255, 255]
const red: Pixel = [255, 0, 51, 255]
const navy: Pixel = [26, 35, 126, 255]
const clear: Pixel = [0, 0, 0, 0]

describe('isDarkIcon', () => {
	it('a near-black logo on a transparent ground is dark', () => {
		expect(isDarkIcon(image([black, 120], [clear, 136]))).toBe(true)
	})

	it('a deep navy logo is dark: it would vanish on a dark tile too', () => {
		expect(isDarkIcon(image([navy, 200], [clear, 56]))).toBe(true)
	})

	it('a black letter on a white square is not dark', () => {
		expect(isDarkIcon(image([white, 200], [black, 56]))).toBe(false)
	})

	it('a saturated red logo is not dark: it still shows on a dark tile', () => {
		expect(isDarkIcon(image([red, 180], [white, 20], [clear, 56]))).toBe(false)
	})

	it('transparent pixels count for nothing, however dark their colour channels', () => {
		expect(isDarkIcon(image([white, 40], [clear, 216]))).toBe(false)
	})

	it('a fully transparent image is not dark', () => {
		expect(isDarkIcon(image([clear, 256]))).toBe(false)
	})
})
