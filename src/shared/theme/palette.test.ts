// Every family's tokens and derived roles, so the C# port (host/Zing.Core/Palette.cs, PaletteTests.cs) colours exactly as the UI does.
import { describe, expect, it } from 'vitest'
import { darkTokens, isAccentTheme, lightTokens } from './accent'
import { accentInk, control, isDark, raised } from './palette'
import vectors from './palette.vectors.json'

describe('palette vectors', () => {
	it.each(vectors.accents)('$accent $mode', ({ accent, mode, tokens, accentInk: ink, control: fill, raised: plate, isDark: dark }) => {
		if (!isAccentTheme(accent)) throw new Error(`Unknown accent ${accent}`)
		const actual = mode === 'light' ? lightTokens(accent) : darkTokens(accent)
		expect(actual).toEqual(tokens)
		expect(accentInk(actual)).toBe(ink)
		expect(control(actual)).toBe(fill)
		expect(raised(actual)).toBe(plate)
		expect(isDark(actual)).toBe(dark)
	})
})
