// Not in Krackn: pins the printf conversions the Swift got from String(format:).

import { describe, expect, it } from 'vitest'
import { formatFixed, formatG } from './cFormat'

describe('formatG matches printf %g', () => {
	it.each([
		[0, '0'],
		[1e12, '1e+12'],
		[123456, '123456'],
		[1234567, '1.23457e+06'],
		[0.0001, '0.0001'],
		[0.00001234, '1.234e-05'],
		[-2.5e-7, '-2.5e-07'],
		[1e100, '1e+100'],
		[100000, '100000'],
	])('%s -> %s', (value, want) => {
		expect(formatG(value)).toBe(want)
	})
})

describe('formatFixed matches printf %.Nf', () => {
	it('keeps the sign of negative zero', () => {
		expect(formatFixed(-0, 0)).toBe('-0')
	})
	it('rounds like toFixed otherwise', () => {
		expect(formatFixed(0.30000000000000004, 1)).toBe('0.3')
	})
})
