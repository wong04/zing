export class MaskSizeError extends Error {
	override name = 'MaskSizeError'
}

/**
 * Sets each pixel's alpha to the mask value times the source alpha (so with an opaque source, alpha is the mask: 0 to 255 from 0 to
 * 1, rounded), and keeps the colour as it was. A pixel the source already made see-through stays at most as opaque as it was, so
 * a transparent PNG does not come back with its hidden colours showing. Returns a new array; `rgba` is not changed.
 * @param mask 0 to 1 for each pixel, row by row; values outside that range count as the nearest end.
 * @throws MaskSizeError the pixels or the mask do not fit `width` by `height`.
 * @throws RangeError a mask value is not a number, which means the model output is broken.
 */
export function applyMask(rgba: Uint8ClampedArray, mask: Float32Array, width: number, height: number): Uint8ClampedArray<ArrayBuffer> {
	const pixels = width * height
	if (rgba.length !== pixels * 4) throw new MaskSizeError(`${rgba.length} colour values do not fit ${width} by ${height} pixels`)
	if (mask.length !== pixels) throw new MaskSizeError(`A mask of ${mask.length} values does not fit ${width} by ${height} pixels`)
	const out = new Uint8ClampedArray(rgba)
	for (let i = 0; i < pixels; i++) {
		const value = mask[i]!
		if (Number.isNaN(value)) throw new RangeError(`The mask has a value that is not a number, at pixel ${i}`)
		const kept = Math.min(1, Math.max(0, value))
		out[i * 4 + 3] = Math.round(kept * rgba[i * 4 + 3]!)
	}
	return out
}

/** Stretches the output so its lowest value is 0 and its highest 1, as the model's own demo does. */
export function normaliseMask(values: Float32Array): Float32Array {
	let low = Infinity
	let high = -Infinity
	for (const value of values) {
		if (Number.isNaN(value)) throw new RangeError('The model gave a value that is not a number')
		if (value < low) low = value
		if (value > high) high = value
	}
	if (!(high > low)) throw new RangeError('The model gave the same value everywhere, so it found no edge')
	const span = high - low
	return values.map((value) => (value - low) / span)
}

/** Bilinear resize of a one-channel picture; pixel centres line up, so a mask does not shift. */
export function resizeMask(source: Float32Array, fromWidth: number, fromHeight: number, toWidth: number, toHeight: number): Float32Array {
	if (source.length !== fromWidth * fromHeight) throw new MaskSizeError(`${source.length} values do not fit ${fromWidth} by ${fromHeight}`)
	if (fromWidth === toWidth && fromHeight === toHeight) return source.slice()
	const out = new Float32Array(toWidth * toHeight)
	const xScale = fromWidth / toWidth
	const yScale = fromHeight / toHeight
	for (let y = 0; y < toHeight; y++) {
		const sourceY = Math.min(fromHeight - 1, Math.max(0, (y + 0.5) * yScale - 0.5))
		const y0 = Math.floor(sourceY)
		const y1 = Math.min(fromHeight - 1, y0 + 1)
		const wy = sourceY - y0
		for (let x = 0; x < toWidth; x++) {
			const sourceX = Math.min(fromWidth - 1, Math.max(0, (x + 0.5) * xScale - 0.5))
			const x0 = Math.floor(sourceX)
			const x1 = Math.min(fromWidth - 1, x0 + 1)
			const wx = sourceX - x0
			const top = source[y0 * fromWidth + x0]! * (1 - wx) + source[y0 * fromWidth + x1]! * wx
			const bottom = source[y1 * fromWidth + x0]! * (1 - wx) + source[y1 * fromWidth + x1]! * wx
			out[y * toWidth + x] = top * (1 - wy) + bottom * wy
		}
	}
	return out
}
