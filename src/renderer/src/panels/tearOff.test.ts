import { describe, expect, it } from 'vitest'
import { TearOffGesture } from './tearOff'

describe('TearOffGesture', () => {
	it('is a tap when released before the hold completes', () => {
		const g = new TearOffGesture()
		g.press()
		expect(g.release()).toBe('tap')
	})

	it('tears off when held, and is not also a tap', () => {
		const g = new TearOffGesture()
		g.press()
		g.hold()
		expect(g.armed).toBe(true)
		expect(g.release()).toBe('tear-off')
	})

	it('stands down when moved before the hold completes: neither a tap nor a tear-off', () => {
		const g = new TearOffGesture()
		g.press()
		g.move(10, 8)
		g.hold()
		expect(g.armed).toBe(false)
		expect(g.release()).toBe('none')
	})

	it('tolerates a small wobble', () => {
		const g = new TearOffGesture()
		g.press()
		g.move(3, 4)
		expect(g.release()).toBe('tap')
	})

	it('lets an armed item travel freely', () => {
		const g = new TearOffGesture()
		g.press()
		g.hold()
		g.move(300, 200)
		expect(g.release()).toBe('tear-off')
	})

	it('does nothing without a press, and resets between gestures', () => {
		const g = new TearOffGesture()
		g.hold()
		expect(g.release()).toBe('none')
		g.press()
		g.hold()
		g.release()
		g.press()
		expect(g.release()).toBe('tap')
	})
})
