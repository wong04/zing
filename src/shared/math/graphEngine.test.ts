// Port of Krackn's Tests/graph/main.swift (54 assertions). Tolerances are the
// Swift ones: `near` defaults to 1e-6.

import { describe, expect, it } from 'vitest'
import { evaluate, makeFunction } from './expression'
import {
	axisLabel,
	defaultViewport,
	extrema,
	graphX,
	graphY,
	intersections,
	panned,
	roots,
	sample,
	screenX,
	screenY,
	tickStep,
	ticks,
	viewportWidth,
	zoomed,
	type PlotFunction,
} from './graphEngine'

function near(a: number, b: number, tol = 1e-6): boolean {
	return Math.abs(a - b) <= tol
}

const vp = defaultViewport
const size = { width: 800, height: 520 }

describe('variables in expressions', () => {
	it('x binds', () => expect(evaluate('x^2', undefined, { x: 3 })).toBe(9))
	it('x in functions', () => expect(near(evaluate('sin(x)', undefined, { x: 0 }), 0)).toBe(true))
	it('implicit mult with x', () => expect(evaluate('2x', undefined, { x: 5 })).toBe(10))
	it('var beats constant e', () => expect(evaluate('e', undefined, { e: 42 })).toBe(42))
	it('pi still works alongside x', () => expect(near(evaluate('x*pi', undefined, { x: 2 }), 2 * Math.PI)).toBe(true))
	it('unbound x should throw', () => expect(() => evaluate('x+1')).toThrow())
})

describe('makeFunction', () => {
	const f = makeFunction('x^2+1')
	it('f(0)', () => expect(f(0)).toBe(1))
	it('f(3)', () => expect(f(3)).toBe(10))
})

describe('viewport mapping', () => {
	it('xMin maps to 0', () => expect(near(screenX(vp.xMin, vp, size), 0)).toBe(true))
	it('xMax maps to width', () => expect(near(screenX(vp.xMax, vp, size), 800)).toBe(true))
	it('yMax maps to top (0)', () => expect(near(screenY(vp.yMax, vp, size), 0)).toBe(true))
	it('yMin maps to bottom', () => expect(near(screenY(vp.yMin, vp, size), 520)).toBe(true))
	it('round-trip x', () => expect(near(graphX(screenX(3.5, vp, size), vp, size), 3.5)).toBe(true))
	it('round-trip y', () => expect(near(graphY(screenY(-2.25, vp, size), vp, size), -2.25)).toBe(true))
})

describe('zoom anchoring', () => {
	const anchor = { x: 4, y: 2 }
	const zoomedIn = zoomed(vp, 0.5, anchor)
	it('zoom halves width', () => expect(near(viewportWidth(zoomedIn), viewportWidth(vp) * 0.5)).toBe(true))
	// The anchor must stay at the same screen position after zooming.
	it('anchor stays put under cursor', () => {
		const beforePx = screenX(anchor.x, vp, size)
		const afterPx = screenX(anchor.x, zoomedIn, size)
		expect(near(beforePx, afterPx, 1e-6), `${beforePx} vs ${afterPx}`).toBe(true)
	})
	it('zoom clamps absurd factors', () => expect(viewportWidth(zoomed(vp, 1e30, anchor))).toBeLessThanOrEqual(1e9))
	it('zoom clamps to non-zero', () => expect(viewportWidth(zoomed(vp, 1e-30, anchor))).toBeGreaterThanOrEqual(1e-9))
	it('pan shifts both bounds', () => expect(panned(vp, 2, -1)).toEqual({ xMin: -8, xMax: 12, yMin: -7.5, yMax: 5.5 }))
})

describe('gridlines', () => {
	it('step for 20 range', () => expect(tickStep(20)).toBe(2))
	it('step for 1 range', () => expect(tickStep(1)).toBe(0.1))
	it('step for 0.05 range', () => expect(near(tickStep(0.05), 0.005)).toBe(true))
	it('step for huge range', () => expect(tickStep(1e6)).toBeGreaterThan(0))
	it('degenerate range is safe', () => expect(tickStep(0)).toBe(1))
	it('ticks span range', () => expect(ticks(-10, 10, 5)).toEqual([-10, -5, 0, 5, 10]))
	it('ticks bail on absurd counts', () => expect(ticks(0, 1e9, 1e-6)).toEqual([]))
	it('label drops fp noise', () => expect(axisLabel(0.30000000000000004, 0.1)).toBe('0.3'))
	it('zero label', () => expect(axisLabel(0, 1)).toBe('0'))
})

describe('sampling', () => {
	const line = sample((x) => x, vp, size)
	it('line is one segment', () => expect(line).toHaveLength(1))
	it('line sampled densely', () => expect(line[0]?.length ?? 0).toBeGreaterThan(500))
	// 1/x must break at the asymptote, not draw through it.
	it('1/x splits at asymptote', () => expect(sample((x) => 1 / x, vp, size).length).toBeGreaterThanOrEqual(2))
	// tan has many asymptotes in [-10,10].
	it('tan splits repeatedly', () => expect(sample(Math.tan, vp, size).length).toBeGreaterThanOrEqual(4))
	// sqrt is undefined for x<0 — no segment should start left of 0.
	const root = sample((x) => (x < 0 ? NaN : Math.sqrt(x)), vp, size)
	it('sqrt only defined half', () => expect(root).toHaveLength(1))
	it('sqrt starts at origin', () => expect(root[0]?.[0]?.x ?? 0).toBeGreaterThanOrEqual(size.width / 2 - 2))
	// A function that throws everywhere yields nothing, and doesn't crash.
	it('all-undefined is empty', () => {
		const undefinedEverywhere: PlotFunction = () => {
			throw new Error('undefined')
		}
		expect(sample(undefinedEverywhere, vp, size)).toEqual([])
	})
	it('zero-size is safe', () => expect(sample((x) => x, vp, { width: 0, height: 0 })).toEqual([]))
})

describe('roots', () => {
	const r = roots((x) => x * x - 4, vp)
	it('x^2-4 has two roots', () => expect(r).toHaveLength(2))
	it('roots near ±2', () => expect(r.every((x) => near(Math.abs(x), 2, 1e-6)), `${r}`).toBe(true))

	// Poles are not roots: tan(x) is zero at multiples of pi, and undefined
	// (not zero) at odd multiples of pi/2.
	const tanRoots = roots(Math.tan, vp)
	it('tan poles not reported as roots', () => {
		const poles = tanRoots.filter((x) => {
			const nearestHalfPi = Math.round(x / (Math.PI / 2))
			return nearestHalfPi % 2 !== 0 && Math.abs(x - nearestHalfPi * Math.PI / 2) < 1e-3
		})
		expect(poles).toEqual([])
	})
	it('tan roots are multiples of pi', () => {
		expect(tanRoots.every((x) => Math.abs(x - Math.round(x / Math.PI) * Math.PI) < 1e-4), `${tanRoots}`).toBe(true)
	})
	it('tan finds the expected zeros', () => expect(tanRoots.length).toBeGreaterThanOrEqual(6))
	// 1/x has no roots at all, despite the sign flip at zero.
	it('1/x has no roots', () => expect(roots((x) => 1 / x, vp)).toEqual([]))
})

describe('intersections', () => {
	// sin(x) and x/3 meet at 0 and two symmetric points.
	const ix = intersections(Math.sin, (x) => x / 3, vp)
	it('sin/line intersections found', () => expect(ix.length).toBeGreaterThanOrEqual(3))
	it('origin is an intersection', () => expect(ix.some((p) => Math.abs(p.x) < 1e-6)).toBe(true))
	it('intersections satisfy both curves', () => expect(ix.every((p) => Math.abs(Math.sin(p.x) - p.x / 3) < 1e-6)).toBe(true))
	it('intersection y matches f(x)', () => expect(ix.every((p) => Math.abs(p.y - Math.sin(p.x)) < 1e-6)).toBe(true))

	// Parallel lines never meet.
	it('parallel lines have none', () => expect(intersections((x) => x + 1, (x) => x - 1, vp)).toEqual([]))

	// x^2 and 4 meet at exactly ±2.
	const quad = intersections((x) => x * x, () => 4, vp)
	it('x^2 = 4 has two solutions', () => expect(quad).toHaveLength(2))
	it('at x = ±2', () => expect(quad.every((p) => Math.abs(Math.abs(p.x) - 2) < 1e-6)).toBe(true))

	// A pole must not be reported where 1/x crosses a line.
	it('1/x never equals 0', () => expect(intersections((x) => 1 / x, () => 0, vp)).toEqual([]))
})

describe('extrema', () => {
	// sin over [-10,10] turns at ±pi/2, ±3pi/2, ...
	const turns = extrema(Math.sin, vp)
	it('sin has several turning points', () => expect(turns.length).toBeGreaterThanOrEqual(5))
	it('turning points sit near ±1', () => expect(turns.every((t) => Math.abs(Math.abs(t.y) - 1) < 0.05)).toBe(true))
	it('both maxima and minima found', () => {
		expect(turns.some((t) => t.isMaximum) && turns.some((t) => !t.isMaximum)).toBe(true)
	})
	// A straight line has none.
	it('line has no turning points', () => expect(extrema((x) => 2 * x, vp)).toEqual([]))
})
