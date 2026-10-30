// Port of `convertColor` from Krackn Features/Utilities/DevTools.swift.
//
// Converts a hex or `rgb()` colour into every notation a designer or
// front-end engineer needs. Like Krackn, `rgb()` channels are taken as given
// (not clamped), and fractional parts are truncated in the integer notations.

export type ColorValues = {
	readonly hex: string
	readonly rgb: string
	readonly rgbFloat: string
	readonly hsl: string
	readonly cmyk: string
}

type RGB = { readonly r: number; readonly g: number; readonly b: number }

const bareHex = /^[0-9a-f]{3,8}$/
const hexDigits = /^[0-9a-f]+$/

/** Null when the input is neither hex (`#abc`, `#aabbcc`, `aabbcc`) nor `rgb(…)`. */
export function convertColor(input: string): ColorValues | null {
	const text = input.trim().toLowerCase()
	const rgb = parseColor(text)
	if (!rgb) return null
	return describe(rgb)
}

function parseColor(text: string): RGB | null {
	if (text.startsWith('#') || bareHex.test(text)) return parseHex(text)
	if (text.startsWith('rgb')) return parseRGBFunction(text)
	return null
}

function parseHex(text: string): RGB | null {
	let hex = text.startsWith('#') ? text.slice(1) : text
	// Expand shorthand (#abc -> #aabbcc).
	if (hex.length === 3) hex = [...hex].map((c) => c + c).join('')
	if (hex.length < 6 || !hexDigits.test(hex)) return null
	const value = parseInt(hex.slice(0, 6), 16)
	return { r: (value >> 16) & 0xff, g: (value >> 8) & 0xff, b: value & 0xff }
}

function parseRGBFunction(text: string): RGB | null {
	const numbers = text
		.split(/[^0-9.]+/)
		.filter((part) => /^(?:\d+\.?\d*|\.\d+)$/.test(part))
		.map(Number)
	const [r, g, b] = numbers
	if (r === undefined || g === undefined || b === undefined) return null
	return { r, g, b }
}

function describe({ r, g, b }: RGB): ColorValues {
	const rn = r / 255
	const gn = g / 255
	const bn = b / 255
	const { hue, saturation, lightness } = hsl(rn, gn, bn)

	const k = 1 - Math.max(rn, gn, bn)
	const denominator = 1 - k
	const ink = (channel: number): number => (denominator === 0 ? 0 : (1 - channel - k) / denominator)

	const [ri, gi, bi] = [r, g, b].map(Math.trunc) as [number, number, number]
	return {
		hex: '#' + [ri, gi, bi].map(hexByte).join(''),
		rgb: `rgb(${ri}, ${gi}, ${bi})`,
		rgbFloat: `${rn.toFixed(3)}, ${gn.toFixed(3)}, ${bn.toFixed(3)}`,
		hsl: `hsl(${scaleTruncated(hue, 360)}, ${scaleTruncated(saturation, 100)}%, ${scaleTruncated(lightness, 100)}%)`,
		cmyk: `cmyk(${scaleTruncated(ink(rn), 100)}%, ${scaleTruncated(ink(gn), 100)}%, ${scaleTruncated(ink(bn), 100)}%, ${scaleTruncated(k, 100)}%)`,
	}
}

function hsl(rn: number, gn: number, bn: number): { hue: number; saturation: number; lightness: number } {
	const max = Math.max(rn, gn, bn)
	const min = Math.min(rn, gn, bn)
	const lightness = (max + min) / 2
	if (max === min) return { hue: 0, saturation: 0, lightness }
	const delta = max - min
	const saturation = lightness > 0.5 ? delta / (2 - max - min) : delta / (max + min)
	let hue: number
	if (max === rn) hue = (gn - bn) / delta + (gn < bn ? 6 : 0)
	else if (max === gn) hue = (bn - rn) / delta + 2
	else hue = (rn - gn) / delta + 4
	return { hue: hue / 6, saturation, lightness }
}

function scaleTruncated(fraction: number, scale: number): number {
	return Math.trunc(fraction * scale)
}

/** `%02X`: at least two upper-case digits. */
function hexByte(value: number): string {
	return value.toString(16).toUpperCase().padStart(2, '0')
}
