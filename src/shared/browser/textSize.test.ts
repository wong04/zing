import { describe, expect, it } from 'vitest'
import { biggerText, smallerText, textSizeCss } from './textSize'

describe('text size', () => {
	it('steps up and down from the default', () => {
		expect(biggerText(100)).toBe(110)
		expect(smallerText(100)).toBe(90)
	})

	it('stops at the ends', () => {
		expect(biggerText(200)).toBe(200)
		expect(smallerText(70)).toBe(70)
	})

	it('treats an unknown value as the default', () => {
		expect(biggerText(103)).toBe(110)
	})

	it('emits an important root override', () => {
		expect(textSizeCss(125)).toContain('font-size: 125% !important')
	})
})
