// Port of Krackn's Features/Utilities/GraphEngine.swift: the maths behind the
// graphing calculator — viewport mapping, gridline choice and curve sampling.
// Kept free of any UI so every part is unit-testable.

import { formatFixed, formatG } from './cFormat'

/** A plotted function. Throwing means "undefined here", like returning NaN. */
export type PlotFunction = (x: number) => number

export interface Size {
	readonly width: number
	readonly height: number
}

export interface Point {
	readonly x: number
	readonly y: number
}

/** The visible region in graph coordinates. */
export interface Viewport {
	readonly xMin: number
	readonly xMax: number
	readonly yMin: number
	readonly yMax: number
}

export const defaultViewport: Viewport = { xMin: -10, xMax: 10, yMin: -6.5, yMax: 6.5 }

export function viewportWidth(viewport: Viewport): number {
	return viewport.xMax - viewport.xMin
}

export function viewportHeight(viewport: Viewport): number {
	return viewport.yMax - viewport.yMin
}

function clamp(value: number, low: number, high: number): number {
	return Math.min(Math.max(value, low), high)
}

/**
 * Scales about a point, for scroll-wheel zoom that keeps the cursor anchored
 * to the same graph coordinate.
 */
export function zoomed(viewport: Viewport, factor: number, anchor: Point): Viewport {
	const width = viewportWidth(viewport)
	const height = viewportHeight(viewport)
	// Clamped so a fast trackpad flick can't collapse the viewport to zero
	// width or blow it up past what a double can represent usefully.
	const newWidth = clamp(width * factor, 1e-9, 1e9)
	const newHeight = clamp(height * factor, 1e-9, 1e9)
	const fx = width === 0 ? 0.5 : (anchor.x - viewport.xMin) / width
	const fy = height === 0 ? 0.5 : (anchor.y - viewport.yMin) / height
	const x0 = anchor.x - fx * newWidth
	const y0 = anchor.y - fy * newHeight
	return { xMin: x0, xMax: x0 + newWidth, yMin: y0, yMax: y0 + newHeight }
}

export function panned(viewport: Viewport, dx: number, dy: number): Viewport {
	return {
		xMin: viewport.xMin + dx,
		xMax: viewport.xMax + dx,
		yMin: viewport.yMin + dy,
		yMax: viewport.yMax + dy,
	}
}

// MARK: - Coordinate mapping

export function screenX(x: number, viewport: Viewport, size: Size): number {
	return (x - viewport.xMin) / viewportWidth(viewport) * size.width
}

/** Screen Y is flipped: graph +y goes up, screen +y goes down. */
export function screenY(y: number, viewport: Viewport, size: Size): number {
	return size.height - (y - viewport.yMin) / viewportHeight(viewport) * size.height
}

export function graphX(px: number, viewport: Viewport, size: Size): number {
	return viewport.xMin + px / size.width * viewportWidth(viewport)
}

export function graphY(py: number, viewport: Viewport, size: Size): number {
	return viewport.yMin + (size.height - py) / size.height * viewportHeight(viewport)
}

// MARK: - Gridlines

/**
 * A "nice" tick step — 1, 2 or 5 times a power of ten — chosen so roughly
 * `target` gridlines span the range. Without this the grid either becomes
 * unreadable when zoomed out or vanishes when zoomed in.
 */
export function tickStep(range: number, target = 10): number {
	if (!(range > 0) || !Number.isFinite(range) || target <= 0) return 1
	const rough = range / target
	const magnitude = Math.pow(10, Math.floor(Math.log10(rough)))
	const normalised = rough / magnitude
	return niceMultiple(normalised) * magnitude
}

function niceMultiple(normalised: number): number {
	if (normalised < 1.5) return 1
	if (normalised < 3) return 2
	if (normalised < 7) return 5
	return 10
}

/** Tick positions covering `min...max` at the given step. */
export function ticks(min: number, max: number, step: number): number[] {
	if (!(step > 0) || !(min < max) || !Number.isFinite(step)) return []
	// Guard against a pathological step producing millions of lines.
	if (!((max - min) / step <= 1000)) return []
	const result: number[] = []
	for (let value = Math.ceil(min / step) * step; value <= max; value += step) result.push(value)
	return result
}

/** Formats an axis label without floating-point noise like `0.30000000004`. */
export function axisLabel(value: number, step: number): string {
	if (Math.abs(value) < step / 1000) return '0'
	const decimals = Math.max(0, -Math.floor(Math.log10(step)))
	if (Math.abs(value) >= 1e5 || (Math.abs(value) < 1e-3 && value !== 0)) return formatG(value)
	return formatFixed(value, Math.min(decimals, 6))
}

// MARK: - Sampling

/**
 * One continuous run of points. A function is split into several segments
 * wherever it's undefined or jumps, so the renderer never draws a false
 * vertical line through an asymptote.
 */
export type Segment = Point[]

/**
 * The finite value of `f` at `x`, or undefined where it has none. A throw is
 * the function saying "undefined here" (sqrt of a negative, an unbound
 * symbol), which for a plot is data, not a failure.
 */
function finiteValue(f: PlotFunction, x: number): number | undefined {
	let y: number
	try {
		y = f(x)
	} catch {
		return undefined
	}
	return Number.isFinite(y) ? y : undefined
}

/**
 * Samples `f` across the viewport.
 *
 * `jumpFactor`: a step larger than this multiple of the viewport height is
 * treated as a discontinuity rather than a steep slope. This is what stops
 * `tan(x)` and `1/x` being joined across their asymptotes.
 */
export function sample(f: PlotFunction, viewport: Viewport, size: Size, samples = 600, jumpFactor = 0.5): Segment[] {
	if (!(size.width > 0) || !(size.height > 0) || samples <= 1) return []

	const height = viewportHeight(viewport)
	const segments: Segment[] = []
	let current: Segment = []
	let previousY: number | undefined
	const jumpLimit = height * jumpFactor
	const closeRun = (): void => {
		if (current.length > 1) segments.push(current)
		current = []
	}

	for (let step = 0; step <= samples; step++) {
		const x = viewport.xMin + viewportWidth(viewport) * step / samples
		const y = finiteValue(f, x)
		if (y === undefined) {
			// Undefined here: close the run and start a new one.
			closeRun()
			previousY = undefined
			continue
		}

		if (previousY !== undefined && Math.abs(y - previousY) > jumpLimit) {
			// Either an asymptote or a genuine near-vertical rise. Breaking is
			// the safe choice: a missing hairline is far less misleading than a
			// line that isn't part of the function.
			closeRun()
		}

		// Clamp far-off-screen values so one huge number doesn't destroy the
		// precision of the rest of the path.
		const clampedY = clamp(y, viewport.yMin - height, viewport.yMax + height)
		current.push({ x: screenX(x, viewport, size), y: screenY(clampedY, viewport, size) })
		previousY = y
	}

	closeRun()
	return segments
}

/** Bisects a sign-changing bracket; returns the midpoint it converged on. */
function bisect(f: PlotFunction, low: number, high: number, lowIsNegative: boolean): number {
	let lo = low
	let hi = high
	let loNegative = lowIsNegative
	for (let i = 0; i < 50; i++) {
		const mid = (lo + hi) / 2
		const midY = finiteValue(f, mid)
		if (midY === undefined) break
		if (midY === 0) {
			lo = mid
			hi = mid
			break
		}
		if ((midY < 0) !== loNegative) {
			hi = mid
		} else {
			lo = mid
			loNegative = midY < 0
		}
	}
	return (lo + hi) / 2
}

/**
 * The zero inside one sample interval, if there is one: the left end when it
 * is exactly zero, otherwise a bisected sign change that really is a zero.
 */
function zeroInBracket(f: PlotFunction, x0: number, y0: number | undefined, x1: number, y1: number | undefined, viewport: Viewport): number | undefined {
	if (y0 === undefined || y1 === undefined) return undefined
	if (y0 === 0) return x0
	if ((y0 < 0) === (y1 < 0)) return undefined

	// A pole also flips sign — tan(x) leaps from +∞ to −∞ at π/2 — so confirm
	// the bracket converged on a zero, not an asymptote.
	const candidate = bisect(f, x0, x1, y0 < 0)
	const value = finiteValue(f, candidate)
	if (value === undefined || !(Math.abs(value) < 1e-3 * Math.max(1, viewportHeight(viewport)))) return undefined
	return candidate
}

/**
 * Finds every x where `f` crosses zero within the viewport.
 *
 * Shared by the roots readout and by intersection finding, which is just the
 * zeros of `f − g`.
 */
export function zeros(f: PlotFunction, viewport: Viewport, samples = 600): number[] {
	const found: number[] = []
	// A sample landing exactly on zero is caught twice — once as the right end
	// of a bracket, once as the left end of the next — so near-identical hits
	// collapse into one.
	const tolerance = viewportWidth(viewport) / samples / 2
	let previousX = viewport.xMin
	let previousY = finiteValue(f, previousX)

	for (let step = 1; step <= samples; step++) {
		const x = viewport.xMin + viewportWidth(viewport) * step / samples
		const y = finiteValue(f, x)
		const root = zeroInBracket(f, previousX, previousY, x, y, viewport)
		if (root !== undefined && !found.some((existing) => Math.abs(root - existing) <= tolerance)) {
			found.push(root)
		}
		previousX = x
		previousY = y
	}
	return found.sort((a, b) => a - b)
}

/** Where a curve crosses the x-axis. */
export function roots(f: PlotFunction, viewport: Viewport, samples = 600): number[] {
	return zeros(f, viewport, samples)
}

class UndefinedValueError extends Error {
	override name = 'UndefinedValueError'
}

/** Where two curves meet — the zeros of their difference. */
export function intersections(f: PlotFunction, g: PlotFunction, viewport: Viewport, samples = 600): Point[] {
	const difference: PlotFunction = (x) => {
		const a = f(x)
		const b = g(x)
		if (!Number.isFinite(a) || !Number.isFinite(b)) throw new UndefinedValueError()
		return a - b
	}
	const height = viewportHeight(viewport)
	return zeros(difference, viewport, samples).flatMap((x) => {
		const y = finiteValue(f, x)
		if (y === undefined || y < viewport.yMin - height || y > viewport.yMax + height) return []
		return [{ x, y }]
	})
}

export interface Extremum {
	readonly x: number
	readonly y: number
	readonly isMaximum: boolean
}

/** Swift's `FloatingPointSign`: -0 counts as minus. */
function isMinus(value: number): boolean {
	return value < 0 || Object.is(value, -0)
}

/** Local minima and maxima, found where the numerical derivative turns. */
export function extrema(f: PlotFunction, viewport: Viewport, samples = 400): Extremum[] {
	const result: Extremum[] = []
	const step = viewportWidth(viewport) / samples
	let previous: Point | undefined
	let previousSlope: number | undefined

	for (let i = 0; i <= samples; i++) {
		const x = viewport.xMin + step * i
		const y = finiteValue(f, x)
		if (y === undefined) {
			previous = undefined
			previousSlope = undefined
			continue
		}
		if (previous !== undefined) {
			const slope = (y - previous.y) / (x - previous.x)
			const before = previousSlope
			if (before !== undefined && isMinus(before) !== isMinus(slope) && Math.abs(slope - before) < viewportHeight(viewport)) {
				// Sign flip in the slope brackets a turning point.
				result.push({ x: previous.x, y: previous.y, isMaximum: before > 0 })
			}
			previousSlope = slope
		}
		previous = { x, y }
	}
	return result
}
