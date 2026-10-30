import { describe, expect, it } from 'vitest'
import { referenceLight } from '@shared/theme/palette'
import { accentVariables } from './applyAccent'

describe('accentVariables', () => {
	it('gives yellow the reference palette, so the default look is unchanged', () => {
		const v = accentVariables('yellow', false)
		expect(v['--bg']).toBe(referenceLight.background)
		expect(v['--accent']).toBe(referenceLight.accent)
	})

	it('changes the accent for another family and keeps every token a colour', () => {
		const blue = accentVariables('blue', true)
		expect(blue['--accent']).not.toBe(accentVariables('yellow', true)['--accent'])
		for (const value of Object.values(blue)) expect(value).toMatch(/^#[0-9a-f]{6}$/i)
	})

	it('refuses an unknown accent instead of leaving the old one', () => {
		expect(() => accentVariables('teal', false)).toThrow(RangeError)
	})
})
