import { describe, expect, it } from 'vitest'
import { EdgeReveal } from './sidebar'

function setup() {
	const changes: boolean[] = []
	const reveal = new EdgeReveal({ onChange: (r) => changes.push(r) })
	reveal.setEnabled(true)
	const at = (x: number, now: number, y = 300, inWindow = true) => reveal.update({ x, y, inWindow, now })
	return { reveal, changes, at }
}

describe('EdgeReveal', () => {
	it('reveals only after a short dwell at the left edge', () => {
		const { reveal, at } = setup()
		at(2, 0)
		expect(reveal.revealed).toBe(false)
		at(2, 40)
		expect(reveal.revealed).toBe(false)
		at(2, 80)
		expect(reveal.revealed).toBe(true)
	})

	it('ignores a sweep past the edge that does not dwell', () => {
		const { reveal, at } = setup()
		at(2, 0)
		at(300, 30)
		at(2, 60)
		at(2, 100)
		expect(reveal.revealed).toBe(false)
	})

	it('ignores the very top row, where the window controls live', () => {
		const { reveal, at } = setup()
		at(2, 0, 0)
		at(2, 200, 0)
		expect(reveal.revealed).toBe(false)
	})

	it('puts the sidebar away once the cursor moves clear of it', () => {
		const { reveal, at } = setup()
		at(2, 0)
		at(2, 100)
		at(150, 200)
		expect(reveal.revealed).toBe(true)
		at(400, 300)
		expect(reveal.revealed).toBe(false)
	})

	it('puts it away when the cursor leaves the window', () => {
		const { reveal, at } = setup()
		at(2, 0)
		at(2, 100)
		at(-50, 200, 300, false)
		expect(reveal.revealed).toBe(false)
	})

	it('does nothing while disabled, and disabling hides it at once', () => {
		const { reveal, changes, at } = setup()
		reveal.setEnabled(false)
		at(2, 0)
		at(2, 100)
		expect(changes).toEqual([])
		reveal.setEnabled(true)
		at(2, 200)
		at(2, 300)
		expect(reveal.revealed).toBe(true)
		reveal.setEnabled(false)
		expect(reveal.revealed).toBe(false)
	})

	it('a peek holds the sidebar out for a moment even with the cursor elsewhere', () => {
		const { reveal, at } = setup()
		reveal.peek(1000)
		expect(reveal.revealed).toBe(true)
		at(900, 1500)
		expect(reveal.revealed).toBe(true)
		at(900, 2500)
		expect(reveal.revealed).toBe(false)
	})
})
