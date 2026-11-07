import { describe, expect, it } from 'vitest'
import { promptWithdrawn, signInLimits, signInOutcome } from './signInOutcome'
import vectors from './signInOutcome.vectors.json'

describe('whether a sign-in worked', () => {
	it.each(vectors.outcomes)('$name', ({ submitted, landed, hasVisiblePasswordBox, expected }) => {
		expect(signInOutcome(submitted, landed, hasVisiblePasswordBox)).toBe(expected)
	})
})

describe('whether a prompt on screen is withdrawn', () => {
	it.each(vectors.withdrawals)('$name', ({ promptOrigin, landed, hasVisiblePasswordBox, expected }) => {
		expect(promptWithdrawn(promptOrigin, landed, hasVisiblePasswordBox)).toBe(expected)
	})
})

describe('the limits of a submit report', () => {
	it('are the ones the vectors give the host', () => {
		expect(signInLimits).toEqual(vectors.limits)
	})
})
