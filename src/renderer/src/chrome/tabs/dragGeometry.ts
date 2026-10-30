import { targetIndexDragging, type Rect } from '@shared/browser/tabReorder'
import { TAB_GAP } from './layout'

/** A chip being dragged within its own section (pinned run, or the loose tabs). */
export interface DragView {
	readonly key: string
	/** Keys of the section in strip order when the drag began. */
	readonly keys: readonly string[]
	readonly startIndex: number
	readonly target: number
	/** How far the chip is drawn from where it began, already kept inside its section. */
	readonly dx: number
	/** Room a neighbour gives way by: the dragged chip's width plus the gap. */
	readonly shift: number
}

/**
 * Where the dragged chip is drawn and which slot it belongs in, from the
 * section's frames measured when the drag began. The chip rides at its start
 * plus the pointer distance, and its slot is judged from where it is drawn
 * (`targetIndexDragging`), the rule Krackn settled on after swaps felt twitchy.
 */
export function dragFrame(keys: readonly string[], startIndex: number, frames: readonly Rect[], pointerDx: number): DragView {
	const first = frames[0] as Rect
	const last = frames[frames.length - 1] as Rect
	const own = frames[startIndex] as Rect
	const dx = Math.min(Math.max(pointerDx, first.x - own.x), last.x + last.width - (own.x + own.width))
	return {
		key: keys[startIndex] as string,
		keys,
		startIndex,
		dx,
		// Judged from the unclamped pointer so the last slot is reachable: clamped, the centre only ever equals its midpoint.
		target: targetIndexDragging(startIndex, own.x + own.width / 2 + pointerDx, frames),
		shift: own.width + TAB_GAP,
	}
}

/** The horizontal offset a chip is drawn at during a drag: the dragged one follows, neighbours make way. */
export function dragOffsetFor(drag: DragView, key: string): number {
	if (key === drag.key) return drag.dx
	const index = drag.keys.indexOf(key)
	if (drag.target > drag.startIndex && index > drag.startIndex && index <= drag.target) return -drag.shift
	if (drag.target < drag.startIndex && index >= drag.target && index < drag.startIndex) return drag.shift
	return 0
}

/** The section's keys after the dragged chip lands. */
export function keysAfterDrop(drag: DragView): string[] {
	const keys = [...drag.keys]
	keys.splice(drag.startIndex, 1)
	keys.splice(drag.target, 0, drag.key)
	return keys
}
