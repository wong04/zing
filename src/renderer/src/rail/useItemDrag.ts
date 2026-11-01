import { useRef, type PointerEvent } from 'react'
import { clearDropMark, dropTargetAt, markDropTarget, type DropTarget } from './dropTarget'

/** Below this, a press that moves is still a click. */
const DRAG_THRESHOLD = 4

interface Held {
	readonly id: string
	readonly x: number
	readonly y: number
	readonly element: HTMLElement
	dragging: boolean
}

export interface ItemDragHandlers {
	readonly onPointerDown: (e: PointerEvent<HTMLElement>) => void
	readonly onPointerMove: (e: PointerEvent<HTMLElement>) => void
	readonly onPointerUp: (e: PointerEvent<HTMLElement>) => void
	readonly onPointerCancel: () => void
}

/**
 * Dragging a saved item (a favourite tile, a pinned item or folder) to another place in the rail. The held
 * element follows the pointer and the place under it is highlighted; release reports where it landed.
 */
export function useItemDrag(onDrop: (id: string, target: DropTarget) => void) {
	const held = useRef<Held | undefined>(undefined)
	const justDropped = useRef(false)

	const release = (h: Held): void => {
		h.element.style.transform = ''
		h.element.classList.remove('dragging')
		clearDropMark()
	}

	const handlersFor = (id: string): ItemDragHandlers => ({
		onPointerDown: (e) => {
			if (e.button !== 0) return
			held.current = { id, x: e.clientX, y: e.clientY, element: e.currentTarget, dragging: false }
			e.currentTarget.setPointerCapture(e.pointerId)
		},
		onPointerMove: (e) => {
			const h = held.current
			if (!h) return
			const dx = e.clientX - h.x
			const dy = e.clientY - h.y
			if (!h.dragging && Math.hypot(dx, dy) < DRAG_THRESHOLD) return
			h.dragging = true
			h.element.classList.add('dragging')
			h.element.style.transform = `translate(${dx}px, ${dy}px)`
			markDropTarget(e.clientX, e.clientY, h.element)
		},
		onPointerUp: (e) => {
			const h = held.current
			held.current = undefined
			if (!h?.dragging) return
			const target = dropTargetAt(e.clientX, e.clientY, h.element)
			release(h)
			justDropped.current = true
			if (target) onDrop(h.id, target)
		},
		onPointerCancel: () => {
			if (held.current) release(held.current)
			held.current = undefined
		},
	})

	/** True once for the click that ends a drag, which must not also open the item. */
	const consumeClick = (): boolean => {
		const was = justDropped.current
		justDropped.current = false
		return was
	}

	return { handlersFor, consumeClick }
}
