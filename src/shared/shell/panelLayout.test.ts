// Port of Krackn Tests/panels (floating-panel placement). Pure, so the rules
// are pinned here rather than eyeballed by dragging windows around.
// The data-shaped cases live in panelLayout.vectors.json, which the C# port (PanelLayoutTests.cs) reads too.
import { readFileSync } from 'node:fs'
import { describe, expect, it } from 'vitest'
import { contains, inset, insetBy, intersects, rect, resolve, resolveAfterDrop, sameRect, type Rect } from './panelLayout'

type RectTuple = readonly [number, number, number, number]

interface Checks {
	readonly unchanged?: number[]
	readonly moved?: number[]
	readonly noOverlaps?: boolean
	readonly inside?: number[]
	readonly insideOrOriginal?: boolean
	readonly disjoint?: [number, number][]
	readonly fieldEquals?: { index: number; field: keyof Rect; value: number }[]
	readonly fieldGreater?: { index: number; field: keyof Rect; than: number }[]
}

interface Case {
	readonly name: string
	readonly op: 'resolve' | 'resolveAfterDrop'
	readonly fixed: number
	readonly bounds: RectTuple
	readonly frames: RectTuple[]
	readonly want: Checks
}

const vectors = JSON.parse(readFileSync(new URL('./panelLayout.vectors.json', import.meta.url), 'utf8')) as { cases: Case[] }

const toRect = (t: RectTuple): Rect => rect(t[0], t[1], t[2], t[3])

function at(frames: readonly Rect[], index: number): Rect {
	const frame = frames[index]
	if (!frame) throw new RangeError(`no frame ${index}`)
	return frame
}

describe('panel placement (shared vectors)', () => {
	it.each(vectors.cases.map((c) => [c.name, c] as const))('%s', (_name, c) => {
		const bounds = toRect(c.bounds)
		const inner = insetBy(bounds, inset, inset)
		const frames = c.frames.map(toRect)
		const result = c.op === 'resolve' ? resolve(c.fixed, frames, bounds) : resolveAfterDrop(c.fixed, frames, bounds)
		const want = c.want

		for (const i of want.unchanged ?? []) expect(result[i]).toEqual(frames[i])
		for (const i of want.moved ?? []) expect(sameRect(at(result, i), at(frames, i))).toBe(false)
		if (want.noOverlaps) {
			expect(result.every((a, i) => result.every((b, j) => j <= i || !intersects(a, b)))).toBe(true)
		}
		for (const i of want.inside ?? []) expect(contains(inner, at(result, i))).toBe(true)
		if (want.insideOrOriginal) {
			expect(result.every((f) => contains(inner, f) || frames.some((g) => sameRect(f, g)))).toBe(true)
		}
		for (const [i, j] of want.disjoint ?? []) expect(intersects(at(result, i), at(result, j))).toBe(false)
		for (const e of want.fieldEquals ?? []) expect(at(result, e.index)[e.field]).toBe(e.value)
		for (const e of want.fieldGreater ?? []) expect(at(result, e.index)[e.field]).toBeGreaterThan(e.than)
	})
})

describe('speed', () => {
	// This runs on every drag frame, so three panels must resolve fast.
	it('three panels resolve in under 4 ms', () => {
		const window = rect(0, 0, 1200, 800)
		const frames = [rect(300, 200, 400, 300), rect(320, 220, 400, 300), rect(340, 240, 400, 300)]
		const start = performance.now()
		for (let i = 0; i < 100; i++) resolve(2, frames, window)
		const perCall = (performance.now() - start) / 100
		expect(perCall).toBeLessThan(4)
	})
})

describe('index guard', () => {
	it('an index with no panel is a programming error, not a silent no-op', () => {
		expect(() => resolve(5, [rect(0, 0, 400, 300)], rect(0, 0, 1200, 800))).toThrow(RangeError)
	})
})
