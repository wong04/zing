import { expect, it } from 'vitest'
import { needsQuitConfirmation } from './quit-policy'

it('asks only when the setting is on and more than one tab is open', () => {
	expect(needsQuitConfirmation(true, 2)).toBe(true)
	expect(needsQuitConfirmation(true, 1)).toBe(false)
	expect(needsQuitConfirmation(false, 5)).toBe(false)
})
