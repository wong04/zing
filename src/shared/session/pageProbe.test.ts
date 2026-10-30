import { describe, expect, it } from 'vitest'
import { PageProbeError, parsePageProbe } from './pageProbe'

describe('parsePageProbe', () => {
	it('accepts the probe shape', () => {
		expect(parsePageProbe({ scrollY: 120, dirty: false })).toEqual({ scrollY: 120, dirty: false })
	})
	it('clamps negative overscroll to the top', () => {
		expect(parsePageProbe({ scrollY: -5, dirty: true }).scrollY).toBe(0)
	})
	it.each([null, 'x', {}, { scrollY: 'a', dirty: false }, { scrollY: Infinity, dirty: false }, { scrollY: 1, dirty: 'no' }])(
		'rejects %j',
		(value) => {
			expect(() => parsePageProbe(value)).toThrow(PageProbeError)
		},
	)
})
