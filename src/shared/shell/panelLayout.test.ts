// Port of Krackn Tests/panels (floating-panel placement). Pure, so the rules
// are pinned here rather than eyeballed by dragging windows around.
import { describe, expect, it } from 'vitest'
import {
	contains,
	gap,
	inset,
	insetBy,
	intersects,
	maxX,
	rect,
	resolve,
	resolveAfterDrop,
	sameRect,
	type Rect,
} from './panelLayout'

const window = rect(0, 0, 1200, 800)
const inner = insetBy(window, inset, inset)

function r(x: number, y: number, w = 400, h = 300): Rect {
	return rect(x, y, w, h)
}

function noOverlaps(frames: readonly Rect[]): boolean {
	return frames.every((a, i) => frames.every((b, j) => j <= i || !intersects(a, b)))
}

function at(frames: readonly Rect[], index: number): Rect {
	const frame = frames[index]
	if (!frame) throw new RangeError(`no frame ${index}`)
	return frame
}

describe('the dropped panel keeps its spot', () => {
	const frames = [r(100, 100), r(250, 150)]
	const result = resolve(1, frames, window)

	it('dropped frame unchanged', () => {
		expect(result[1]).toEqual(frames[1])
	})
	it('the other one moved', () => {
		expect(sameRect(at(result, 0), at(frames, 0))).toBe(false)
	})
	it('nothing overlaps afterwards', () => {
		expect(noOverlaps(result)).toBe(true)
	})
	it('pushed panel is still inside the window', () => {
		expect(contains(inner, at(result, 0))).toBe(true)
	})
})

describe("panels that don't touch are left alone", () => {
	it("untouched panels don't move", () => {
		const frames = [r(20, 20), r(600, 400)]
		expect(resolve(0, frames, window)).toEqual(frames)
	})
})

describe('the push is the shortest one available', () => {
	// B overlaps A's right edge by 40pt. The cheapest escape is right by
	// 40 + gap, not down by a whole panel height.
	const a = r(100, 100)
	const b = r(460, 100)
	const result = resolve(0, [a, b], window)

	it('pushed right, not down', () => {
		expect(at(result, 1).y).toBe(b.y)
		expect(at(result, 1).x).toBeGreaterThan(b.x)
	})
	it('cleared by exactly the gap', () => {
		expect(at(result, 1).x).toBe(maxX(a) + gap)
	})
})

describe('a panel pinned against the edge escapes the other way', () => {
	// B is at the right edge and A lands on it from the left: there's no room
	// to go right, so it has to go left, up or down.
	const b = r(790, 250)
	const a = r(700, 250)
	const result = resolve(0, [a, b], window)

	it('still inside the window', () => {
		expect(contains(inner, at(result, 1))).toBe(true)
	})
	it('no overlap', () => {
		expect(intersects(at(result, 0), at(result, 1))).toBe(false)
	})
})

describe('three panels in a pile all end up apart', () => {
	const frames = [r(300, 200), r(320, 220), r(340, 240)]
	const result = resolve(2, frames, window)

	it('no overlaps between any pair', () => {
		expect(noOverlaps(result)).toBe(true)
	})
	it('all inside the window', () => {
		expect(result.every((f) => contains(inner, f) || frames.some((g) => sameRect(f, g)))).toBe(true)
	})
})

describe('a window too small to separate them leaves them be', () => {
	it("unplaceable panel isn't thrown off-screen", () => {
		const tiny = rect(0, 0, 500, 350)
		const frames = [r(10, 10), r(60, 30)]
		expect(resolve(1, frames, tiny)[0]).toEqual(frames[0])
	})
})

describe("on a drop, the dropped panel may slide when that's the only way", () => {
	// The gallery case that exposed this: two big panels in a 1240pt window,
	// the dropped one in the middle, no room on either side of it.
	const big = rect(0, 0, 1240, 800)
	const json = rect(380, 102, 520, 580)
	const diag = rect(460, 182, 480, 540)

	it('mid-drag the held panel never moves', () => {
		expect(resolve(1, [json, diag], big)[1]).toEqual(diag)
	})

	const dropped = resolveAfterDrop(1, [json, diag], big)
	it('after the drop they no longer overlap', () => {
		expect(intersects(at(dropped, 0), at(dropped, 1))).toBe(false)
	})
	it('both are inside the window', () => {
		const area = insetBy(big, inset, inset)
		expect(contains(area, at(dropped, 0)) && contains(area, at(dropped, 1))).toBe(true)
	})
})

describe('speed', () => {
	// This runs on every drag frame, so three panels must resolve fast.
	it('three panels resolve in under 4 ms', () => {
		const frames = [r(300, 200), r(320, 220), r(340, 240)]
		const start = performance.now()
		for (let i = 0; i < 100; i++) resolve(2, frames, window)
		const perCall = (performance.now() - start) / 100
		expect(perCall).toBeLessThan(4)
	})
})

describe('index guard', () => {
	it('an index with no panel is a programming error, not a silent no-op', () => {
		expect(() => resolve(5, [r(0, 0)], window)).toThrow(RangeError)
	})
})
