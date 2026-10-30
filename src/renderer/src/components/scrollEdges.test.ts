import { describe, expect, it } from 'vitest'
import { scrollEdges } from './scrollEdges'

describe('scrollEdges', () => {
	it('shows only the bottom fade at the top of long content', () => {
		expect(scrollEdges({ offset: 0, contentHeight: 900, viewportHeight: 300 })).toEqual({ top: false, bottom: true })
	})

	it('shows both fades in the middle', () => {
		expect(scrollEdges({ offset: 200, contentHeight: 900, viewportHeight: 300 })).toEqual({ top: true, bottom: true })
	})

	it('shows only the top fade at the end', () => {
		expect(scrollEdges({ offset: 600, contentHeight: 900, viewportHeight: 300 })).toEqual({ top: true, bottom: false })
	})

	it('shows nothing when everything fits', () => {
		expect(scrollEdges({ offset: 0, contentHeight: 200, viewportHeight: 300 })).toEqual({ top: false, bottom: false })
	})

	it('treats a one-pixel remainder as rounding', () => {
		expect(scrollEdges({ offset: 599.5, contentHeight: 900, viewportHeight: 300 })).toEqual({ top: true, bottom: false })
		expect(scrollEdges({ offset: 1, contentHeight: 900, viewportHeight: 300 }).top).toBe(false)
	})
})
