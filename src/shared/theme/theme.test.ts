// Port of Krackn Tests/theme.
import { describe, expect, it } from 'vitest'
import { accentLabel, AccentTheme, accentThemes, darkTokens, lightTokens } from './accent'
import { contrastRatio, hexFromHSL, hexFromRGB, InvalidHexColorError, relativeLuminance, rgbFromHex, wcagAA } from './color'
import { accentInk, control, generatePalette, lightProfile, raised, type PaletteTokens } from './palette'

const modes: readonly [string, (theme: AccentTheme) => PaletteTokens][] = [
	['light', lightTokens],
	['dark', darkTokens],
]

describe('the reference palette is preserved exactly', () => {
	// The whole promise of the accent system is that picking "Yellow" leaves
	// the original design untouched. Generated values would land within a
	// point or two of these, which is invisible but still a change nobody
	// asked for.
	const yl = lightTokens(AccentTheme.yellow)
	const yd = darkTokens(AccentTheme.yellow)
	it.each([
		['light background', yl.background, '#F7F2E9'],
		['light accent', yl.accent, '#D9B872'],
		['light outline', yl.outline, '#2A2420'],
		['light text', yl.text, '#241F19'],
		['dark background', yd.background, '#1A1714'],
		['dark accent', yd.accent, '#C9A45E'],
		['dark text', yd.text, '#F0E7D8'],
	])('%s', (_name, got, want) => {
		expect(got.toUpperCase()).toBe(want)
	})
})

describe('every family is legible in both modes', () => {
	// WCAG AA is 4.5:1 for body text. Anything below that isn't a style
	// choice, it's a bug — and a generated palette can drift there without
	// anyone noticing.
	const cases = accentThemes.flatMap((theme) =>
		modes.flatMap(([mode, tokensFor]) => {
			const tokens = tokensFor(theme)
			const pairs: [string, string, string][] = [
				['text on background', tokens.text, tokens.background],
				['text on surface', tokens.text, tokens.surface],
				['secondary on background', tokens.textSecondary, tokens.background],
				['onAccent on accent', tokens.onAccent, tokens.accent],
				['text on well', tokens.text, tokens.well],
				// The accent used as a glyph/link colour — the original accent
				// is under 2:1 here, which is why `accentInk` exists.
				['accentInk on surface', accentInk(tokens), tokens.surface],
				['accentInk on selection', accentInk(tokens), tokens.selection],
				['text on control', tokens.text, control(tokens)],
				['text on raised', tokens.text, raised(tokens)],
			]
			return pairs.map(([label, fg, bg]) => [`${accentLabel(theme)} ${mode} ${label}`, fg, bg] as const)
		}),
	)
	it('covers 7 families × 2 modes × 9 pairs', () => {
		expect(cases).toHaveLength(126)
	})
	it.each(cases)('%s', (_name, fg, bg) => {
		expect(contrastRatio(fg, bg)).toBeGreaterThanOrEqual(wcagAA)
	})
})

describe('a selected segment reads as raised, not sunken', () => {
	// The dark surface is darker than the dark well, so a selection plate
	// drawn in `surface` looked pressed in. `raised` must out-luminate
	// `control` in both modes.
	const cases = accentThemes.flatMap((theme) =>
		modes.map(([mode, tokensFor]) => [`${accentLabel(theme)} ${mode} raised above control`, tokensFor(theme)] as const),
	)
	it.each(cases)('%s', (_name, t) => {
		expect(relativeLuminance(raised(t))).toBeGreaterThan(relativeLuminance(control(t)))
	})
})

describe('families are actually distinct', () => {
	// A generator bug that ignored the hue would produce seven identical
	// palettes and every contrast test above would still pass.
	it(`all ${accentThemes.length} families have a distinct accent colour`, () => {
		const accents = accentThemes.map((theme) => lightTokens(theme).accent)
		expect(new Set(accents).size).toBe(accentThemes.length)
	})
})

describe('mono is neutral, the others are not', () => {
	const saturation = (hex: string): number => {
		const { r, g, b } = rgbFromHex(hex)
		const mx = Math.max(r, g, b)
		const mn = Math.min(r, g, b)
		return mx === 0 ? 0 : (mx - mn) / mx
	}
	it('mono accent is near-neutral', () => {
		expect(saturation(lightTokens(AccentTheme.mono).accent)).toBeLessThan(0.12)
	})
	it.each(accentThemes.filter((theme) => theme !== AccentTheme.mono))('%s accent carries colour', (theme) => {
		expect(saturation(lightTokens(theme).accent)).toBeGreaterThan(0.2)
	})
})

describe('HSL conversion', () => {
	it.each([
		['pure white', 0, 0, 1, '#FFFFFF'],
		['pure black', 0, 0, 0, '#000000'],
		['mid grey', 0, 0, 0.5, '#808080'],
		['pure red', 0, 1, 0.5, '#FF0000'],
		['pure green', 120, 1, 0.5, '#00FF00'],
		['pure blue', 240, 1, 0.5, '#0000FF'],
		// Hue must wrap rather than clamp.
		['hue wraps past 360', 480, 1, 0.5, '#00FF00'],
		['negative hue wraps', -120, 1, 0.5, '#0000FF'],
	])('%s', (_name, h, s, l, want) => {
		expect(hexFromHSL(h, s, l)).toBe(want)
	})
})

describe('hex parsing round-trips', () => {
	it.each(['#F7F2E9', '#000000', '#FFFFFF', '#D9B872', '#1A1714'])('round-trip %s', (hex) => {
		expect(hexFromRGB(rgbFromHex(hex))).toBe(hex)
	})
	// Krackn degraded malformed input to black ("rather than crash"). Zing
	// throws instead: every token is generated, so a malformed one is a bug
	// to surface, not a colour to guess.
	it('malformed hex is refused', () => {
		expect(() => rgbFromHex('not-a-colour')).toThrow(InvalidHexColorError)
	})
})

// Zing additions, for the Phase 5 generator work.
describe('the generator', () => {
	it('lifting is what keeps a dark hue legible', () => {
		const purple = lightTokens(AccentTheme.purple)
		expect(contrastRatio(purple.onAccent, purple.accent)).toBeGreaterThanOrEqual(4.6)
	})
	it('regenerating the yellow hue lands within a few points of the reference, not on it', () => {
		const regenerated = generatePalette(lightProfile, 38, 1)
		const reference = lightTokens(AccentTheme.yellow)
		const distance = (a: string, b: string): number => {
			const x = rgbFromHex(a)
			const y = rgbFromHex(b)
			return Math.max(Math.abs(x.r - y.r), Math.abs(x.g - y.g), Math.abs(x.b - y.b)) * 255
		}
		expect(distance(regenerated.background, reference.background)).toBeLessThanOrEqual(3)
		expect(distance(regenerated.text, reference.text)).toBeLessThanOrEqual(3)
	})
	it('accepts a lower-case hex', () => {
		expect(rgbFromHex('ffffff')).toEqual({ r: 1, g: 1, b: 1 })
	})
})
