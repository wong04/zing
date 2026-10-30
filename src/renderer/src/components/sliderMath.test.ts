import { describe, expect, it } from 'vitest'
import { nudge, ratioOf, valueAtPointer, valueAtRatio } from './sliderMath'

const quality = { min: 10, max: 100, step: 5 }

describe('slider math', () => {
	it('maps a ratio to a stepped value inside the range', () => {
		expect(valueAtRatio(0, quality)).toBe(10)
		expect(valueAtRatio(1, quality)).toBe(100)
		expect(valueAtRatio(0.5, quality)).toBe(55)
		expect(valueAtRatio(0.52, quality)).toBe(55)
		expect(valueAtRatio(-3, quality)).toBe(10)
		expect(valueAtRatio(9, quality)).toBe(100)
	})

	it('reads the pointer through the inset track', () => {
		const range = { min: 0, max: 100, step: 1 }
		expect(valueAtPointer(9, 218, 18, range)).toBe(0)
		expect(valueAtPointer(209, 218, 18, range)).toBe(100)
		expect(valueAtPointer(109, 218, 18, range)).toBe(50)
		expect(valueAtPointer(-40, 218, 18, range)).toBe(0)
	})

	it('has nowhere to move on a track narrower than the knob', () => {
		expect(valueAtPointer(5, 10, 18, quality)).toBe(10)
	})

	it('nudges by whole steps and stops at the ends', () => {
		expect(nudge(80, 1, quality)).toBe(85)
		expect(nudge(80, -2, quality)).toBe(70)
		expect(nudge(100, 1, quality)).toBe(100)
		expect(nudge(10, -1, quality)).toBe(10)
	})

	it('does not leak floating-point noise from fractional steps', () => {
		const fine = { min: 0, max: 1, step: 0.1 }
		expect(nudge(0.2, 1, fine)).toBe(0.3)
	})

	it('reports where the value sits in the range', () => {
		expect(ratioOf(55, quality)).toBe(0.5)
		expect(ratioOf(500, quality)).toBe(1)
	})
})
