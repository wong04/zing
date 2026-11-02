import { describe, expect, it } from 'vitest'
import { dragRoom } from './dragRoom'

const frame = (top: number, height = 38) => ({ top, height })

describe('dragRoom', () => {
	it('is the distance from the held row to the next one', () => {
		const frames = new Map([['a', frame(0)], ['b', frame(30)], ['c', frame(60)]])
		expect(dragRoom(['a', 'b', 'c'], 'b', frames)).toBe(30)
	})

	it('is the distance from the previous row for the last row', () => {
		const frames = new Map([['a', frame(0)], ['b', frame(30)], ['c', frame(60)]])
		expect(dragRoom(['a', 'b', 'c'], 'c', frames)).toBe(30)
	})

	it('is the row height when it is the only row', () => {
		expect(dragRoom(['a'], 'a', new Map([['a', frame(0)]]))).toBe(38)
	})

	it('skips a neighbour that has no frame', () => {
		const frames = new Map([['a', frame(0)], ['c', frame(60)]])
		expect(dragRoom(['a', 'b', 'c'], 'a', frames)).toBe(60)
	})

	it('is 0 for a held row that has no frame', () => {
		const frames = new Map([['a', frame(0)], ['b', frame(30)]])
		expect(dragRoom(['a', 'b', 'c'], 'c', frames)).toBe(0)
	})
})
