import { useCallback, useRef, useState, type PointerEvent } from 'react'
import type { Rect } from '@shared/browser/tabReorder'
import type { Slot } from '@shared/tabs/slots'
import { dragFrame, type DragView } from './dragGeometry'
import { isPinnedSlot } from './layout'

/** Pointer travel before a press becomes a drag, so a plain click still selects. */
const DRAG_THRESHOLD = 4

export interface Dropped {
	readonly slot: Slot
	/** Index within the slot's own section (pinned run, or loose tabs). */
	readonly from: number
	readonly to: number
	readonly drag: DragView
}

interface Press {
	readonly slot: Slot
	readonly pointerId: number
	readonly startX: number
	started: boolean
	frames: Rect[]
	keys: string[]
}

export interface SlotDragHandlers {
	onPointerDown: (e: PointerEvent<HTMLElement>) => void
	onPointerMove: (e: PointerEvent<HTMLElement>) => void
	onPointerUp: (e: PointerEvent<HTMLElement>) => void
	onPointerCancel: (e: PointerEvent<HTMLElement>) => void
}

/**
 * Live drag-to-reorder for the tab strip. The chip follows the pointer by a
 * CSS offset and its neighbours make way; nothing reaches main until release,
 * when the final slot is committed in one call. Pointer capture keeps the chip
 * receiving moves wherever the pointer goes, and since chips are `no-drag`
 * regions the window itself never moves.
 */
export function useSlotDrag(slots: readonly Slot[], onDrop: (dropped: Dropped) => void) {
	const [drag, setDrag] = useState<DragView>()
	const elements = useRef(new Map<string, HTMLElement>())
	const press = useRef<Press | undefined>(undefined)
	const latest = useRef<DragView | undefined>(undefined)
	const justDragged = useRef(false)

	const register = useCallback(
		(key: string) => (element: HTMLElement | null) => {
			if (element) elements.current.set(key, element)
			else elements.current.delete(key)
		},
		[],
	)

	const begin = (p: Press): void => {
		const section = slots.filter((s) => isPinnedSlot(s) === isPinnedSlot(p.slot))
		p.keys = section.map((s) => s.key)
		p.frames = p.keys.map((key) => (elements.current.get(key) as HTMLElement).getBoundingClientRect())
		p.started = true
	}

	const down = (slot: Slot, e: PointerEvent<HTMLElement>): void => {
		if (e.button !== 0) return
		press.current = { slot, pointerId: e.pointerId, startX: e.clientX, started: false, frames: [], keys: [] }
	}

	const move = (e: PointerEvent<HTMLElement>): void => {
		const p = press.current
		if (!p || p.pointerId !== e.pointerId) return
		const dx = e.clientX - p.startX
		if (!p.started) {
			if (Math.abs(dx) < DRAG_THRESHOLD) return
			begin(p)
			e.currentTarget.setPointerCapture(e.pointerId)
		}
		const startIndex = p.keys.indexOf(p.slot.key)
		if (startIndex < 0) return
		const view = dragFrame(p.keys, startIndex, p.frames, dx)
		latest.current = view
			setDrag(view)
	}

	const finish = (e: PointerEvent<HTMLElement>, cancelled: boolean): void => {
		const p = press.current
		if (!p || p.pointerId !== e.pointerId) return
		press.current = undefined
		if (!p.started) return
		if (e.currentTarget.hasPointerCapture(e.pointerId)) e.currentTarget.releasePointerCapture(e.pointerId)
		// The click that follows a drag's pointerup must not also toggle a stack.
		justDragged.current = true
		setTimeout(() => (justDragged.current = false), 0)
		const current = latest.current
		latest.current = undefined
		setDrag(undefined)
		if (cancelled || !current || current.target === current.startIndex) return
		onDrop({ slot: p.slot, from: current.startIndex, to: current.target, drag: current })
	}

	const consumeClick = (): boolean => justDragged.current

	const handlersFor = (slot: Slot): SlotDragHandlers => ({
		onPointerDown: (e) => down(slot, e),
		onPointerMove: move,
		onPointerUp: (e) => finish(e, false),
		onPointerCancel: (e) => finish(e, true),
	})

	return { drag, register, handlersFor, consumeClick }
}
