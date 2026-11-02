// Every family's tokens and derived roles, so the C# port (host/Zing.Core/Palette.cs, PaletteTests.cs) colours exactly as the UI does.
import { describe, expect, it } from 'vitest'
import { CUSTOM_ACCENT, darkTokens, hueProfile, isAccentTheme, lightTokens, seedProfile, themeTokens } from './accent'
import { accentInk, control, isDark, raised, stackTokens } from './palette'
import vectors from './palette.vectors.json'

describe('palette vectors', () => {
	it.each(vectors.accents)('$accent $mode', ({ accent, mode, tokens, accentInk: ink, control: fill, raised: plate, isDark: dark, stack }) => {
		if (!isAccentTheme(accent)) throw new Error(`Unknown accent ${accent}`)
		const actual = mode === 'light' ? lightTokens(accent) : darkTokens(accent)
		expect(actual).toEqual(tokens)
		expect(accentInk(actual)).toBe(ink)
		expect(control(actual)).toBe(fill)
		expect(raised(actual)).toBe(plate)
		expect(isDark(actual)).toBe(dark)
		expect(stackTokens(hueProfile(accent), mode === 'dark')).toEqual(stack)
	})
})

describe('seed vectors', () => {
	it.each(vectors.seeds)('$seed $mode', ({ seed, mode, tokens, stack }) => {
		const dark = mode === 'dark'
		expect(themeTokens(CUSTOM_ACCENT, seed, dark)).toEqual(tokens)
		expect(stackTokens(seedProfile(seed), dark)).toEqual(stack)
	})
})
