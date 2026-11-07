import { MaskSizeError } from './alphaMask'

/** The square side, in pixels, the model takes. */
export const MODEL_SIZE = 1024

export interface Normalisation {
	readonly mean: readonly [number, number, number]
	readonly std: readonly [number, number, number]
	/** Divide by the picture's brightest value first (a dim picture then keeps the model's expected range). */
	readonly divideByMax: boolean
}

/**
 * The model's input from `width` by `height` RGBA pixels: one plane of red values, then green, then blue (the layout the
 * model asks for), each value turned into `(value / 255 - mean) / std`. The alpha channel is ignored.
 * @throws MaskSizeError the pixels do not fit the size.
 */
export function toModelInput(rgba: Uint8ClampedArray, width: number, height: number, norm: Normalisation): Float32Array {
	const pixels = width * height
	if (rgba.length !== pixels * 4) throw new MaskSizeError(`${rgba.length} colour values do not fit ${width} by ${height} pixels`)
	// A black picture has no range to stretch, and 255 is the plain 8-bit range.
	let scale = 255
	if (norm.divideByMax) {
		let brightest = 0
		for (let i = 0; i < pixels; i++) brightest = Math.max(brightest, rgba[i * 4]!, rgba[i * 4 + 1]!, rgba[i * 4 + 2]!)
		scale = brightest === 0 ? 1 : brightest
	}
	const out = new Float32Array(pixels * 3)
	for (let channel = 0; channel < 3; channel++) {
		const mean = norm.mean[channel]!
		const std = norm.std[channel]!
		const plane = channel * pixels
		for (let i = 0; i < pixels; i++) out[plane + i] = (rgba[i * 4 + channel]! / scale - mean) / std
	}
	return out
}
