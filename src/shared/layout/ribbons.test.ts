import { describe, expect, it } from 'vitest'
import { ribbonLayout } from './ribbons'

describe('ribbon layout', () => {
	it.each([
		[16, 224, 4, 12],
		[5, 224, 5, 0],
		[6, 224, 4, 2],
		[4, 144, 2, 2],
		[3, 144, 3, 0],
		[2, 36, 0, 2],
		[1, 36, 1, 0],
		[0, 224, 0, 0],
	])('%i favourites in %i px', (count, width, shown, more) => {
		expect(ribbonLayout(count, width)).toEqual({ shown, more })
	})
})

describe('favourites bar ribbon layout', () => {
	const bar = { width: 30, gap: 5 } as const

	it.each([
		[0, 200, 0, 0],
		[5, 200, 5, 0],
		[6, 200, 4, 2],
		[16, 200, 4, 12],
		[1, 0, 1, 0],
		[2, 0, 0, 2],
		[2, 70, 2, 0],
		[3, 70, 1, 2],
	])('%i favourites in %i px', (count, width, shown, more) => {
		expect(ribbonLayout(count, width, bar)).toEqual({ shown, more })
	})
})
