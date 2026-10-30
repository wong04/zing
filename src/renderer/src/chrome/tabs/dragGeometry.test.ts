import { describe, expect, it } from 'vitest'
import type { Rect } from '@shared/browser/tabReorder'
import { dragFrame, dragOffsetFor, keysAfterDrop } from './dragGeometry'

const keys = ['a', 'b', 'c', 'd']
// Four 100-wide chips with a 2 gap.
const frames: Rect[] = keys.map((_, i) => ({ x: i * 102, y: 0, width: 100, height: 30 }))

describe('dragFrame', () => {
	it('keeps the slot until the dragged chip crosses a neighbour centre', () => {
		expect(dragFrame(keys, 1, frames, 40).target).toBe(1)
		expect(dragFrame(keys, 1, frames, 60).target).toBe(1)
		expect(dragFrame(keys, 1, frames, 110).target).toBe(2)
	})

	it('moves several slots in one gesture, in both directions', () => {
		expect(dragFrame(keys, 0, frames, 320).target).toBe(3)
		expect(dragFrame(keys, 3, frames, -320).target).toBe(0)
	})

	it('keeps the dragged chip inside its section', () => {
		expect(dragFrame(keys, 0, frames, -500).dx).toBe(0)
		expect(dragFrame(keys, 3, frames, 500).dx).toBe(0)
		expect(dragFrame(keys, 0, frames, 5000).dx).toBe(3 * 102)
	})
})

describe('dragOffsetFor', () => {
	it('slides neighbours back by one chip as the dragged chip passes them', () => {
		const drag = dragFrame(keys, 0, frames, 210)
		expect(drag.target).toBe(2)
		expect(dragOffsetFor(drag, 'a')).toBe(210)
		expect(dragOffsetFor(drag, 'b')).toBe(-102)
		expect(dragOffsetFor(drag, 'c')).toBe(-102)
		expect(dragOffsetFor(drag, 'd')).toBe(0)
	})

	it('slides them forward when dragging left', () => {
		const drag = dragFrame(keys, 3, frames, -210)
		expect(dragOffsetFor(drag, 'b')).toBe(102)
		expect(dragOffsetFor(drag, 'c')).toBe(102)
		expect(dragOffsetFor(drag, 'a')).toBe(0)
	})
})

describe('keysAfterDrop', () => {
	it('lists the section as it will be once the chip lands', () => {
		expect(keysAfterDrop(dragFrame(keys, 0, frames, 210))).toEqual(['b', 'c', 'a', 'd'])
	})
})
