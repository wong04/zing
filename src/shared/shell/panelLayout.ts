// Port of Krackn App/PanelLayout.swift (`PanelLayout`).
//
// Where floating panels go so they never sit on top of each other. Pure
// geometry over rectangles, so the rules can be tested directly: the panel
// being placed always wins its spot, and everything it lands on moves the
// *shortest* distance that clears it while staying inside the window.

export type Rect = {
	readonly x: number
	readonly y: number
	readonly width: number
	readonly height: number
}

/** Breathing room left between two panels after one is pushed. */
export const gap = 12
/** Panels keep this far inside the window edge when they're pushed. */
export const inset = 8

/** Depth of the search: how many of each panel's nearest free spots are tried. */
const spotsPerPanel = 10
/** On a drop, moving the dropped panel costs this much more than moving the others. */
const fixedMoveWeight = 3

type Layout = { cost: number; frames: Rect[] }

export function rect(x: number, y: number, width: number, height: number): Rect {
	return { x, y, width, height }
}

export function maxX(r: Rect): number {
	return r.x + r.width
}

export function maxY(r: Rect): number {
	return r.y + r.height
}

export function insetBy(r: Rect, dx: number, dy: number): Rect {
	return rect(r.x + dx, r.y + dy, r.width - dx * 2, r.height - dy * 2)
}

/** CGRect semantics: rectangles that only share an edge don't intersect. */
export function intersects(a: Rect, b: Rect): boolean {
	return a.x < maxX(b) && b.x < maxX(a) && a.y < maxY(b) && b.y < maxY(a)
}

/** CGRect semantics: edges are inclusive. */
export function contains(outer: Rect, inner: Rect): boolean {
	return inner.x >= outer.x && inner.y >= outer.y && maxX(inner) <= maxX(outer) && maxY(inner) <= maxY(outer)
}

export function sameRect(a: Rect, b: Rect): boolean {
	return a.x === b.x && a.y === b.y && a.width === b.width && a.height === b.height
}

/**
 * Resolves overlaps around `frames[fixed]`, the panel being dragged or just
 * opened. It keeps exactly the frame it was given; every other panel moves the
 * least it can. A panel that can't be cleared without leaving `bounds` — a
 * window too small for everything — stays where it was rather than being
 * thrown off-screen.
 */
export function resolve(fixed: number, frames: readonly Rect[], bounds: Rect): Rect[] {
	const fixedFrame = frameAt(frames, fixed)
	const others = othersNearestFirst(fixed, frames)
	const best = cheapestLayout(frames, others, bounds, { index: fixed, frame: fixedFrame, cost: 0 }, null)
	if (best) return best.frames
	return placeOneAtATime(fixed, frames, others, bounds)
}

/**
 * Like `resolve`, for when the panel has been *released* rather than being
 * held mid-drag, so it no longer has to stay under the cursor.
 *
 * Nothing may fit around the dropped panel where it is — two large panels in
 * a window that only has room for them side by side, say, with the dropped
 * one in the middle. Then it may slide too, so that both can fit. Its movement
 * is weighted heavily: it should move as little as possible, and only when
 * that's the only way.
 */
export function resolveAfterDrop(fixed: number, frames: readonly Rect[], bounds: Rect): Rect[] {
	const home = frameAt(frames, fixed)
	const others = othersNearestFirst(fixed, frames)
	let best = cheapestLayout(frames, others, bounds, { index: fixed, frame: home, cost: 0 }, null)
	if (best) return best.frames

	for (const spot of freeSpots(home, [], bounds)) {
		const moved = Math.hypot(spot.x - home.x, spot.y - home.y) * fixedMoveWeight
		best = cheapestLayout(frames, others, bounds, { index: fixed, frame: spot, cost: moved }, best)
	}
	if (best) return best.frames
	return placeOneAtATime(fixed, frames, others, bounds)
}

/**
 * Every place `frame` could go without overlapping `occupied`, nearest first.
 * Its current position counts if it's already clear — even when it hangs
 * partly off the window, since the user may have put it there.
 */
export function freeSpots(frame: Rect, occupied: readonly Rect[], bounds: Rect): Rect[] {
	const area = insetBy(bounds, inset, inset)
	const xs = [frame.x, area.x, maxX(area) - frame.width]
	const ys = [frame.y, area.y, maxY(area) - frame.height]
	// Butting up against any occupied panel's edge, plus the window edges so a
	// panel in a corner can slide along the edge. Every combination is tried,
	// which finds diagonal escapes a one-axis push misses.
	for (const other of occupied) {
		xs.push(other.x - frame.width - gap, maxX(other) + gap)
		ys.push(other.y - frame.height - gap, maxY(other) + gap)
	}
	const spots: { spot: Rect; distance: number }[] = []
	for (const x of xs) {
		for (const y of ys) {
			const candidate = rect(x, y, frame.width, frame.height)
			if (occupied.some((o) => intersects(o, candidate))) continue
			const isHome = x === frame.x && y === frame.y
			if (!isHome && !contains(area, candidate)) continue
			spots.push({ spot: candidate, distance: Math.hypot(x - frame.x, y - frame.y) })
		}
	}
	const seen = new Set<string>()
	return spots
		.sort((a, b) => a.distance - b.distance)
		.map((s) => s.spot)
		.filter((s) => {
			const key = `${s.x},${s.y}`
			if (seen.has(key)) return false
			seen.add(key)
			return true
		})
}

/**
 * A joint search, not one panel at a time. Greedy placement fails in exactly
 * the case that matters: the first panel pushed takes the middle of the only
 * free column, and the second then has nowhere to go even though the two of
 * them fit stacked. Branch and bound over each panel's few nearest free spots
 * finds the layout that moves things least in total. With the three-panel cap
 * that's a few thousand rectangle tests at most — cheap enough for every drag
 * frame.
 */
function cheapestLayout(
	frames: readonly Rect[],
	others: readonly number[],
	bounds: Rect,
	start: { index: number; frame: Rect; cost: number },
	bestSoFar: Layout | null,
): Layout | null {
	let best = bestSoFar
	const current = [...frames]
	current[start.index] = start.frame

	const visit = (depth: number, placed: Rect[], cost: number): void => {
		if (best && cost >= best.cost) return
		const index = others[depth]
		if (index === undefined) {
			best = { cost, frames: [...current] }
			return
		}
		const home = frameAt(frames, index)
		for (const spot of freeSpots(home, placed, bounds).slice(0, spotsPerPanel)) {
			current[index] = spot
			visit(depth + 1, [...placed, spot], cost + Math.hypot(spot.x - home.x, spot.y - home.y))
		}
		current[index] = home
	}
	visit(0, [start.frame], start.cost)
	return best
}

/**
 * No arrangement clears everything — the window is too small for the panels.
 * Do what can be done one at a time and leave the rest where they were,
 * rather than throwing anything off-screen.
 */
function placeOneAtATime(fixed: number, frames: readonly Rect[], others: readonly number[], bounds: Rect): Rect[] {
	const result = [...frames]
	const placed = [frameAt(frames, fixed)]
	for (const index of others) {
		const spot = freeSpots(frameAt(frames, index), placed, bounds)[0]
		if (spot) result[index] = spot
		placed.push(frameAt(result, index))
	}
	return result
}

/** Nearest first: the panel that was hit squarely gets first pick of free space, and the search meets good answers early. */
function othersNearestFirst(fixed: number, frames: readonly Rect[]): number[] {
	const target = frameAt(frames, fixed)
	return frames
		.map((_, index) => index)
		.filter((index) => index !== fixed)
		.sort((a, b) => centreDistance(frameAt(frames, a), target) - centreDistance(frameAt(frames, b), target))
}

function centreDistance(a: Rect, b: Rect): number {
	return Math.hypot(a.x + a.width / 2 - (b.x + b.width / 2), a.y + a.height / 2 - (b.y + b.height / 2))
}

function frameAt(frames: readonly Rect[], index: number): Rect {
	const frame = frames[index]
	if (!frame) throw new RangeError(`No panel at index ${index} (have ${frames.length})`)
	return frame
}
