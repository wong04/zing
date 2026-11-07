import { describe, expect, it } from 'vitest'
import { signInLimits, signInOutcome } from './signInOutcome'
import vectors from './signInOutcome.vectors.json'

describe('whether a sign-in worked', () => {
	it.each(vectors.outcomes)('$name', ({ submitted, landed, hasVisiblePasswordBox, expected }) => {
		expect(signInOutcome(submitted, landed, hasVisiblePasswordBox)).toBe(expected)
	})
})

describe('the limits of a submit report', () => {
	it('are the ones the vectors give the host', () => {
		expect(signInLimits).toEqual(vectors.limits)
	})
})
