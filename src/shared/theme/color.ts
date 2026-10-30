// Colour maths from Krackn App/Accent.swift (`PaletteTokens.hexFromHSL`,
// `rgb(from:)`, `contrastRatio`, `mix`). Colours are plain `#RRGGBB` strings
// so the same values can be emitted as CSS and used by the UI.

export class InvalidHexColorError extends Error {
	override name = 'InvalidHexColorError'
}

/** sRGB channels, each 0…1. */
export type RGB = { readonly r: number; readonly g: number; readonly b: number }

/** WCAG AA for body text. Anything below that isn't a style choice, it's a bug. */
export const wcagAA = 4.5

const hexPattern = /^#?([0-9A-Fa-f]{6})$/

/** HSL → `#RRGGBB`. Hue in degrees (wraps, never clamps); saturation and lightness 0…1 (clamped). */
export function hexFromHSL(hue: number, saturation: number, lightness: number): string {
	const h = (((hue % 360) + 360) % 360) / 360
	const s = clamp01(saturation)
	const l = clamp01(lightness)
	if (s <= 0) {
		const v = Math.round(l * 255)
		return hexFromBytes(v, v, v)
	}

	const q = l < 0.5 ? l * (1 + s) : l + s - l * s
	const p = 2 * l - q
	const channel = (offset: number): number => {
		let t = h + offset
		if (t < 0) t += 1
		if (t > 1) t -= 1
		if (t < 1 / 6) return p + (q - p) * 6 * t
		if (t < 1 / 2) return q
		if (t < 2 / 3) return p + (q - p) * (2 / 3 - t) * 6
		return p
	}
	return hexFromBytes(Math.round(channel(1 / 3) * 255), Math.round(channel(0) * 255), Math.round(channel(-1 / 3) * 255))
}

/** Parses `#RRGGBB` (the `#` is optional). Throws on anything else rather than guessing a colour. */
export function rgbFromHex(hex: string): RGB {
	const digits = hexPattern.exec(hex)?.[1]
	if (digits === undefined) throw new InvalidHexColorError(`Not a #RRGGBB colour: ${JSON.stringify(hex)}`)
	const value = parseInt(digits, 16)
	return { r: ((value >> 16) & 0xff) / 255, g: ((value >> 8) & 0xff) / 255, b: (value & 0xff) / 255 }
}

export function hexFromRGB({ r, g, b }: RGB): string {
	return hexFromBytes(Math.round(r * 255), Math.round(g * 255), Math.round(b * 255))
}

/** WCAG relative luminance. */
export function relativeLuminance(hex: string): number {
	const { r, g, b } = rgbFromHex(hex)
	return 0.2126 * linear(r) + 0.7152 * linear(g) + 0.0722 * linear(b)
}

/** WCAG contrast ratio, 1…21, order-independent. */
export function contrastRatio(a: string, b: string): number {
	const first = relativeLuminance(a)
	const second = relativeLuminance(b)
	return (Math.max(first, second) + 0.05) / (Math.min(first, second) + 0.05)
}

/** Blend of two colours in sRGB channel space; `amount` 0 is `a`, 1 is `b` (clamped). */
export function mix(a: string, b: string, amount: number): string {
	const x = rgbFromHex(a)
	const y = rgbFromHex(b)
	const t = clamp01(amount)
	const channel = (p: number, q: number): number => Math.round((p + (q - p) * t) * 255)
	return hexFromBytes(channel(x.r, y.r), channel(x.g, y.g), channel(x.b, y.b))
}

/** Luma of the gamma-encoded channels: a quick light-or-dark test, not a contrast measure. */
export function gammaLuma(hex: string): number {
	const { r, g, b } = rgbFromHex(hex)
	return 0.2126 * r + 0.7152 * g + 0.0722 * b
}

function linear(c: number): number {
	return c <= 0.03928 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4)
}

function clamp01(value: number): number {
	return Math.min(Math.max(value, 0), 1)
}

function hexFromBytes(r: number, g: number, b: number): string {
	return '#' + [r, g, b].map((v) => v.toString(16).toUpperCase().padStart(2, '0')).join('')
}
