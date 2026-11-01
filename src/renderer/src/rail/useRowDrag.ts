import { useCallback, useRef, useState, type PointerEvent } from 'react'
import { clearDropMark, dropTargetAt, markDropTarget, type DropTarget } from './dropTarget'

/** Below this, a press that moves is still a click. */
const DRAG_THRESHOLD = 4

export interface RowDrag {
	readonly key: string
	/** How far the held row has moved from where it was picked up. */
	readonly dy: number
	/** Where it would land among the rows, counting from 0. */
	readonly to: number
	/** The room the held row leaves: its height and the gap after it. */
	readonly room: number
}

/** The pointer handlers a row spreads onto itself. */
export interface RowHandlers {
	readonly onPointerDown: (e: PointerEvent<HTMLElement>) => void
	readonly onPointerMove: (e: PointerEvent<HTMLElement>) => void
	readonly onPointerUp: (e: PointerEvent<HTMLElement>) => void
	readonly onPointerCancel: () => void
}

interface Pickup {
	readonly key: string
	readonly y: number
	/** Each row's top and height when the drag began; the rows slide during it, so they are measured once. */
	readonly frames: ReadonlyMap<string, { readonly top: number; readonly height: number }>
	dragging: boolean
}

/**
 * Vertical drag-to-reorder for the rail's rows (the strip's useSlotDrag works along X). The held row
 * follows the pointer; the others slide aside to show where it would land; release reports the place.
 */
/**
 * @param onDrop the held row's key and its new place among the rows, or where it landed outside them
 * (a folder of Favourites or Pinned) when it was dropped there
 */
export function useRowDrag(keys: readonly string[], onDrop: (key: string, to: number, outside: DropTarget | undefined) => void) {
	const elements = useRef(new Map<string, HTMLElement>())
	const pickup = useRef<Pickup | undefined>(undefined)
	const justDropped = useRef(false)
	const [drag, setDrag] = useState<RowDrag>()

	const register = useCallback(
		(key: string) => (element: HTMLElement | null) => {
			if (element) elements.current.set(key, element)
			else elements.current.delete(key)
		},
		[],
	)

	const targetOf = (held: Pickup, dy: number): number => {
		const own = held.frames.get(held.key)
		if (!own) return keys.indexOf(held.key)
		const centre = own.top + own.height / 2 + dy
		return keys.filter((k) => k !== held.key).filter((k) => {
			const frame = held.frames.get(k)
			return frame !== undefined && frame.top + frame.height / 2 < centre
		}).length
	}

	const handlersFor = (key: string): RowHandlers => ({
		onPointerDown: (e: PointerEvent<HTMLElement>): void => {
			if (e.button !== 0) return
			const frames = new Map<string, { top: number; height: number }>()
			for (const k of keys) {
				const rect = elements.current.get(k)?.getBoundingClientRect()
				if (rect) frames.set(k, { top: rect.top, height: rect.height })
			}
			pickup.current = { key, y: e.clientY, frames, dragging: false }
			e.currentTarget.setPointerCapture(e.pointerId)
		},
		onPointerMove: (e: PointerEvent<HTMLElement>): void => {
			const held = pickup.current
			if (!held) return
			const dy = e.clientY - held.y
			if (!held.dragging && Math.abs(dy) < DRAG_THRESHOLD) return
			held.dragging = true
			setDrag({ key: held.key, dy, to: targetOf(held, dy), room: (held.frames.get(held.key)?.height ?? 0) + 2 })
			markDropTarget(e.clientX, e.clientY, e.currentTarget)
		},
		onPointerUp: (e: PointerEvent<HTMLElement>): void => {
			const held = pickup.current
			pickup.current = undefined
			if (!held?.dragging) return
			justDropped.current = true
			setDrag(undefined)
			const landed = dropTargetAt(e.clientX, e.clientY, e.currentTarget)
			clearDropMark()
			onDrop(held.key, targetOf(held, e.clientY - held.y), landed?.kind === 'folder' ? landed : undefined)
		},
		onPointerCancel: (): void => {
			clearDropMark()
			pickup.current = undefined
			setDrag(undefined)
		},
	})

	/** How far a row is moved right now: the held one follows the pointer, the ones it passed make room. */
	const offsetOf = (key: string): number => {
		if (!drag) return 0
		if (key === drag.key) return drag.dy
		const from = keys.indexOf(drag.key)
		const at = keys.indexOf(key)
		if (from < at && at <= drag.to) return -drag.room
		if (drag.to <= at && at < from) return drag.room
		return 0
	}

	/** True once for the click that ends a drag, which must not also act as a click. */
	const consumeClick = (): boolean => {
		const was = justDropped.current
		justDropped.current = false
		return was
	}

	return { drag, register, handlersFor, offsetOf, consumeClick }
}
