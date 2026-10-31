// Port of Krackn `Tests/tabs`: reorder geometry and tab widths. The WebViewHost
// checks in that suite (hit-test shield, rebuilt containers, fullscreen) are AppKit-only.
// The data-shaped cases live in tabReorder.vectors.json, which the C# port (TabReorderTests.cs) reads too.
import { readFileSync } from 'node:fs'
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
	type TabBarLayoutInput,
	type TabBarLayoutResult,
} from './tabReorder'

interface GeometryCase {
	readonly name: string
	readonly op: 'targetIndex' | 'targetIndexDragging' | 'centerAfterMove'
	readonly widths: number[]
	readonly transpose?: boolean
	readonly axis?: Axis
	readonly position?: number
	readonly index?: number
	readonly center?: number
	readonly from?: number
	readonly to?: number
	readonly want: number
}

interface LayoutCase {
	readonly name: string
	readonly input: TabBarLayoutInput
	readonly want: TabBarLayoutResult
}

const vectors = JSON.parse(readFileSync(new URL('./tabReorder.vectors.json', import.meta.url), 'utf8')) as {
	geometry: GeometryCase[]
	layouts: LayoutCase[]
}

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

function perform(c: GeometryCase): number {
	const row = layoutRow(c.widths)
	const used = c.transpose ? row.map(transpose) : row
	switch (c.op) {
		case 'targetIndex':
			return targetIndex(c.position!, used, c.axis)
		case 'targetIndexDragging':
			return targetIndexDragging(c.index!, c.center!, used, c.axis)
		case 'centerAfterMove':
			return centerAfterMove(c.from!, c.to!, used, c.axis)
	}
}

describe('reorder geometry (shared vectors)', () => {
	it.each(vectors.geometry.map((c) => [c.name, c] as const))('%s', (_name, c) => {
		expect(perform(c)).toBe(c.want)
	})
})

describe('tab widths (shared vectors)', () => {
	it.each(vectors.layouts.map((c) => [c.name, c] as const))('%s', (_name, c) => {
		const got = tabBarLayout(c.input)
		expect(got.compact).toEqual(c.want.compact)
		expect(got.looseWidths).toHaveLength(c.want.looseWidths.length)
		got.looseWidths.forEach((width, i) => expect(width).toBeCloseTo(c.want.looseWidths[i] as number, 9))
	})
})

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

describe('the chip\'s centre after a move is predicted, not read from stale frames', () => {
	it('prediction matches the reflowed layout', () => {
		const predicted = centerAfterMove(1, 2, frames)
		expect(Math.trunc(predicted)).toBe(Math.trunc(midX(layoutRow([80, 60, 120, 100])[2] as Rect)))
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
