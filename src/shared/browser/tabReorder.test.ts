// Port of Krackn `Tests/tabs`: reorder geometry and tab widths. The WebViewHost
// checks in that suite (hit-test shield, rebuilt containers, fullscreen) are AppKit-only.
import { describe, expect, it } from 'vitest'
import {
	centerAfterMove,
	dividerWidth,
	iconWidth,
	minimumTitled,
	pinnedWidth,
	tabBarLayout,
	targetIndex,
	targetIndexDragging,
	type Axis,
	type Rect,
} from './tabReorder'

// Chips of the given widths laid out left to right with 4pt gaps starting at x=8.
function layoutRow(widths: readonly number[]): Rect[] {
	let x = 8
	return widths.map((width) => {
		const frame = { x, y: 0, width, height: 28 }
		x += width + 4
		return frame
	})
}

function transpose(frame: Rect): Rect {
	return { x: frame.y, y: frame.x, width: frame.height, height: frame.width }
}

const midX = (frame: Rect) => frame.x + frame.width / 2
const frames = layoutRow([80, 120, 60, 100])
const f = (i: number) => frames[i] as Rect

// Mirrors what the gesture does each frame: find the target slot, move, repeat.
function simulate(order: string[], widths: number[], drag: number, toX: number): string[] {
	const tabs = [...order]
	let current = drag
	const layout = () => layoutRow(tabs.map((t) => widths[order.indexOf(t)] as number))
	// Step the cursor across in small increments, like real drag frames.
	let px = midX(layout()[current] as Rect)
	while (px < toX) {
		px += 3
		const target = targetIndex(px, layout())
		if (target !== current) {
			const [item] = tabs.splice(current, 1)
			tabs.splice(target, 0, item as string)
			current = target
		}
	}
	return tabs
}

describe('cursor target slot', () => {
	it('far left', () => expect(targetIndex(0, frames)).toBe(0))
	it('left half of chip 0', () => expect(targetIndex(20, frames)).toBe(0))
	it('past chip 0 mid', () => expect(targetIndex(60, frames)).toBe(1))
	it('past chip 1 mid', () => expect(targetIndex(170, frames)).toBe(2))
	it('past chip 2 mid', () => expect(targetIndex(250, frames)).toBe(3))
	it('far right', () => expect(targetIndex(9999, frames)).toBe(3))
	it('empty frames', () => expect(targetIndex(50, [])).toBe(0))

	// Boundary: exactly at a midpoint should be deterministic, not flickery
	it('midpoint boundary is stable', () => {
		const m0 = midX(f(0))
		expect([targetIndex(m0 - 0.5, frames), targetIndex(m0 + 0.5, frames)]).toEqual([0, 1])
	})

	it('sweep is monotonic (no flicker)', () => {
		let last = -1
		for (let px = 0; px <= 400; px += 2) {
			const t = targetIndex(px, frames)
			expect(t).toBeGreaterThanOrEqual(last)
			last = t
		}
	})
})

describe('a full drag', () => {
	const names = ['A', 'B', 'C', 'D']
	const widths = [80, 120, 60, 100]

	it('drag A fully right', () => {
		expect(simulate(names, widths, 0, 400)).toEqual(['B', 'C', 'D', 'A'])
	})
	it('partial drag keeps all tabs', () => {
		const r2 = simulate(names, widths, 0, 150)
		expect(r2).toHaveLength(4)
		expect(new Set(r2)).toEqual(new Set(names))
	})
	it('drag C left keeps all tabs', () => {
		expect(new Set(simulate(names, widths, 2, 60))).toEqual(new Set(names))
	})
})

describe('a dragged tab swaps only once it crosses its neighbour\'s centre', () => {
	// Chip 1 (120 wide, mid 152) dragged right. Chip 2 spans 216–276, mid 246.
	it('nudged 10pt right: stays', () => expect(targetIndexDragging(1, 162, frames)).toBe(1))
	it('just short of neighbour\'s centre: stays', () => expect(targetIndexDragging(1, 245, frames)).toBe(1))
	it('past neighbour\'s centre: swaps', () => expect(targetIndexDragging(1, 247, frames)).toBe(2))
	it('far right: last slot', () => expect(targetIndexDragging(1, 9999, frames)).toBe(3))
	it('dragged left past chip 0\'s centre', () => expect(targetIndexDragging(1, 47, frames)).toBe(0))
	it('dragged left, not far enough', () => expect(targetIndexDragging(1, 49, frames)).toBe(1))
})

describe('the chip\'s centre after a move is predicted, not read from stale frames', () => {
	// Moving chip 1 (x 92–212) into slot 2: it ends where chip 2 ended (276).
	const after12 = centerAfterMove(1, 2, frames)

	it('moved right: lands flush with the old right edge', () => {
		expect(Math.trunc(after12)).toBe(Math.trunc(f(2).x + f(2).width - 60))
	})
	it('moved left: lands flush with the old left edge', () => {
		expect(Math.trunc(centerAfterMove(1, 0, frames))).toBe(Math.trunc(f(0).x + 60))
	})
	// And the prediction matches a real reflow of the new order.
	it('prediction matches the reflowed layout', () => {
		expect(Math.trunc(after12)).toBe(Math.trunc(midX(layoutRow([80, 60, 120, 100])[2] as Rect)))
	})
})

// Not in Krackn: the same geometry down a vertical strip gives the same answers.
describe('vertical strip', () => {
	const column = frames.map(transpose)
	const y: Axis = 'y'

	it('cursor target slots match the horizontal strip', () => {
		for (const position of [0, 20, 60, 170, 250, 9999]) {
			expect(targetIndex(position, column, y)).toBe(targetIndex(position, frames))
		}
	})
	it('dragged-chip slots match the horizontal strip', () => {
		for (const centre of [162, 245, 247, 9999, 47, 49]) {
			expect(targetIndexDragging(1, centre, column, y)).toBe(targetIndexDragging(1, centre, frames))
		}
	})
	it('predicted centres match the horizontal strip', () => {
		expect(centerAfterMove(1, 2, column, y)).toBe(centerAfterMove(1, 2, frames))
		expect(centerAfterMove(1, 0, column, y)).toBe(centerAfterMove(1, 0, frames))
	})
	it('reads the axis it is given, not x', () => {
		// The row's chips all span y 0–28, so 20pt down is past every centre.
		expect(targetIndex(20, frames, y)).toBe(3)
		expect(targetIndex(20, frames)).toBe(0)
	})
})

describe('tabs share the full width, compressing to icons when crowded', () => {
	const near = (a: number, b: number) => Math.abs(a - b) < 0.5
	const sum = (values: number[]) => values.reduce((a, b) => a + b, 0)

	it('one tab spans the row', () => {
		const one = tabBarLayout({ total: 1000, pinned: 0, loose: 1, activeLoose: 0 })
		expect(near(one.looseWidths[0] as number, 1000) && one.compact.length === 1 && !one.compact[0]).toBe(true)
	})
	const four = tabBarLayout({ total: 1000, pinned: 0, loose: 4, activeLoose: 2 })
	it('four tabs split it equally', () => {
		expect(four.looseWidths.every((w) => near(w, (1000 - 6) / 4))).toBe(true)
	})
	it('and keep their titles', () => {
		expect(four.compact.every((c) => !c)).toBe(true)
	})
	it('pinned icons come off the top first', () => {
		const pinned = tabBarLayout({ total: 1000, pinned: 3, loose: 2, activeLoose: 0 })
		const room = 1000 - 3 * (pinnedWidth + 2) - dividerWidth
		expect(near(sum(pinned.looseWidths) + 2, room)).toBe(true)
	})
	const crowd = tabBarLayout({ total: 1000, pinned: 0, loose: 20, activeLoose: 5 })
	it('crowded: the active tab keeps a title', () => {
		expect(!crowd.compact[5] && (crowd.looseWidths[5] as number) >= minimumTitled).toBe(true)
	})
	it('crowded: the rest are icons', () => {
		expect(crowd.compact.every((c, i) => i === 5 || c)).toBe(true)
	})
	it('crowded: everything still fits the row', () => {
		expect(sum(crowd.looseWidths) + 19 * 2).toBeLessThanOrEqual(1000.5)
	})
	it('never narrower than an icon', () => {
		const huge = tabBarLayout({ total: 600, pinned: 0, loose: 60, activeLoose: 0 })
		expect(huge.looseWidths.every((w) => w >= iconWidth)).toBe(true)
	})
	it('active tab pinned: loose tabs share evenly as icons', () => {
		const pinnedActive = tabBarLayout({ total: 500, pinned: 1, loose: 10, activeLoose: null })
		expect(
			pinnedActive.compact.every((c) => c) && new Set(pinnedActive.looseWidths.map(Math.trunc)).size === 1,
		).toBe(true)
	})
	it('icon tabs leave room for the close button beside the icon', () => {
		const icons = tabBarLayout({ total: 1000, pinned: 0, loose: 20, activeLoose: 0 })
		expect(icons.looseWidths.every((w, i) => i === 0 || w >= 40)).toBe(true)
	})
})
