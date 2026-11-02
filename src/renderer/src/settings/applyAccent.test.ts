import { describe, expect, it } from 'vitest'
import { DEFAULT_CUSTOM_COLOR } from '@shared/settings'
import { referenceLight } from '@shared/theme/palette'
import { accentVariables } from '@shared/theme/accentVariables'

/** Fixed across accents, so a "danger" never turns green with the theme. */
const STATUS_VARIABLES = ['--positive', '--caution', '--danger']

describe('accentVariables', () => {
	it('gives yellow the reference palette, so the default look is unchanged', () => {
		const v = accentVariables('yellow', DEFAULT_CUSTOM_COLOR, false)
		expect(v['--bg']).toBe(referenceLight.background)
		expect(v['--accent']).toBe(referenceLight.accent)
	})

	it('changes the accent for another family and keeps every token a colour', () => {
		const blue = accentVariables('blue', DEFAULT_CUSTOM_COLOR, true)
		expect(blue['--accent']).not.toBe(accentVariables('yellow', DEFAULT_CUSTOM_COLOR, true)['--accent'])
		for (const value of Object.values(blue)) expect(value).toMatch(/^#[0-9a-f]{6}$/i)
	})

	it('takes the seed colour for the custom accent', () => {
		expect(accentVariables('custom', '#3366CC', false)['--accent']).not.toBe(accentVariables('yellow', DEFAULT_CUSTOM_COLOR, false)['--accent'])
	})

	it('refuses an unknown accent instead of leaving the old one', () => {
		expect(() => accentVariables('teal', DEFAULT_CUSTOM_COLOR, false)).toThrow(RangeError)
	})

	it('status colours are not themed', () => {
		for (const accent of ['blue', 'custom'])
			for (const dark of [false, true]) {
				const keys = Object.keys(accentVariables(accent, '#3366CC', dark))
				for (const status of STATUS_VARIABLES) expect(keys).not.toContain(status)
			}
	})
})
