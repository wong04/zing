// Port of Krackn's `TabBarLayout` and `TabReorder` (Features/Browser/TabReorder.swift).
// The reorder geometry takes an axis so a vertical tab strip can reuse it; 'x' is Krackn's.

/** Same shape as Electron's `Rectangle` and the fields of a DOM `DOMRect`. */
export interface Rect {
	x: number
	y: number
	width: number
	height: number
}

export type Axis = 'x' | 'y'

function minOf(rect: Rect, axis: Axis): number {
	return axis === 'x' ? rect.x : rect.y
}

function lengthOf(rect: Rect, axis: Axis): number {
	return axis === 'x' ? rect.width : rect.height
}

function maxOf(rect: Rect, axis: Axis): number {
	return minOf(rect, axis) + lengthOf(rect, axis)
}

function midOf(rect: Rect, axis: Axis): number {
	return minOf(rect, axis) + lengthOf(rect, axis) / 2
}

/**
 * Which slot a cursor at `position` belongs in, given each chip's frame in
 * strip order.
 *
 * A chip claims a slot once the cursor passes its midpoint, which is what
 * makes neighbours swap cleanly instead of flickering while the pointer
 * sits on a boundary.
 */
export function targetIndex(position: number, frames: readonly Rect[], axis: Axis = 'x'): number {
	if (frames.length === 0) return 0
	const index = frames.findIndex((frame) => position < midOf(frame, axis))
	return index < 0 ? frames.length - 1 : index
}

/**
 * Which slot the dragged chip belongs in, judged by where the chip *is
 * drawn* rather than where the cursor is.
 *
 * The old rule asked which chip's midpoint the cursor had passed, and the
 * list included the dragged chip's own frame — so nudging a tab a few
 * points right of its own centre already claimed the next slot, and the
 * strip swapped on the slightest movement. Here a neighbour gives way
 * only once the dragged chip's centre crosses *the neighbour's* centre.
 */
export function targetIndexDragging(
	index: number,
	visualCenter: number,
	frames: readonly Rect[],
	axis: Axis = 'x',
): number {
	if (index < 0 || index >= frames.length) return index
	const mid = (i: number): number => midOf(frames[i] as Rect, axis)
	let target = index
	while (target + 1 < frames.length && visualCenter > mid(target + 1)) target += 1
	while (target - 1 >= 0 && visualCenter < mid(target - 1)) target -= 1
	return target
}

/**
 * Where the dragged chip's centre will sit once it has moved from
 * `from` to `to`, computed from the frames *before* the move.
 *
 * Needed because the chips' real frames only arrive after the next
 * layout pass. Re-anchoring on those stale frames straight after a swap
 * put the chip a whole slot away from the cursor, so it lurched and then
 * fought the pointer — the "tabs won't drag" symptom.
 */
export function centerAfterMove(from: number, to: number, frames: readonly Rect[], axis: Axis = 'x'): number {
	const source = frames[from]
	if (source === undefined) return 0
	const target = frames[to]
	if (target === undefined || to === from) return midOf(source, axis)
	const length = lengthOf(source, axis)
	if (to > from) return maxOf(target, axis) - length / 2
	return minOf(target, axis) + length / 2
}

// MARK: - Tab widths

export const pinnedWidth = 34
/**
 * Room for the close button at the left *and* the icon beside it, so an
 * icon-only tab never has to swap one for the other.
 */
export const iconWidth = 40
/** Below this a title is mostly ellipsis; drop it and show the icon alone. */
export const minimumTitled = 110
export const dividerWidth = 9

export interface TabBarLayoutResult {
	looseWidths: number[]
	/** Per loose tab: icon only. */
	compact: boolean[]
}

export interface TabBarLayoutInput {
	total: number
	pinned: number
	loose: number
	/** Index among the loose tabs, or null when the active tab is pinned. */
	activeLoose: number | null
	spacing?: number
}

/**
 * How wide each tab is. Safari's rule: tabs share the whole row equally —
 * one tab spans the window — and when there are too many for titles to be
 * readable, the one you're on keeps a readable width and the rest shrink to
 * their icons. Pinned tabs are always icons, at the left.
 */
export function tabBarLayout({ total, pinned, loose, activeLoose, spacing = 2 }: TabBarLayoutInput): TabBarLayoutResult {
	if (loose <= 0) return { looseWidths: [], compact: [] }
	let available = total - pinned * (pinnedWidth + spacing)
	if (pinned > 0) available -= dividerWidth
	const gaps = spacing * (loose - 1)
	const equal = (available - gaps) / loose
	if (equal >= minimumTitled) {
		return { looseWidths: Array<number>(loose).fill(equal), compact: Array<boolean>(loose).fill(false) }
	}
	if (activeLoose === null || loose <= 1) {
		const width = Math.max(equal, iconWidth)
		return { looseWidths: Array<number>(loose).fill(width), compact: Array<boolean>(loose).fill(true) }
	}
	let activeWidth = Math.min(Math.max(available * 0.25, 160), 240)
	let others = (available - activeWidth - gaps) / (loose - 1)
	if (others < iconWidth) {
		// Icons are at their smallest: the active tab gives back width so
		// the row still fits, keeping its title while it can.
		others = iconWidth
		activeWidth = Math.max(available - gaps - others * (loose - 1), iconWidth)
	}
	const looseWidths = Array<number>(loose).fill(others)
	looseWidths[activeLoose] = activeWidth
	const compact = Array<boolean>(loose).fill(others < minimumTitled)
	compact[activeLoose] = activeWidth < minimumTitled
	return { looseWidths, compact }
}
