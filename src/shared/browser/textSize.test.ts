import { describe, expect, it } from 'vitest'
import { DEFAULT_TEXT_SIZE, TEXT_SIZE_STEPS, biggerText, smallerText, textSizeCss } from './textSize'
import vectors from './textSize.vectors.json'

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

// The same cases run against host/Zing.Core/TextSize.cs (TextSizeTests.cs).
describe('text size vectors', () => {
	it('has the same steps and default', () => {
		expect(vectors.steps).toEqual([...TEXT_SIZE_STEPS])
		expect(vectors.default).toBe(DEFAULT_TEXT_SIZE)
	})
	it.each(vectors.sizes)('$percent', ({ percent, bigger, smaller, css }) => {
		expect(biggerText(percent)).toBe(bigger)
		expect(smallerText(percent)).toBe(smaller)
		expect(textSizeCss(percent)).toBe(css)
	})
})
