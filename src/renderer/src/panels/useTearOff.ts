import { useRef, useState, type KeyboardEvent, type MouseEvent, type PointerEvent } from 'react'
import type { Point } from '@shared/shell/shellState'
import { HOLD_MS, TearOffGesture } from './tearOff'

export interface TearOffHandlers {
	readonly onPointerDown: (e: PointerEvent) => void
	readonly onPointerMove: (e: PointerEvent) => void
	readonly onPointerUp: (e: PointerEvent) => void
	readonly onPointerCancel: (e: PointerEvent) => void
	readonly onKeyDown: (e: KeyboardEvent) => void
	readonly onContextMenu: (e: MouseEvent) => void
}

/**
 * Wires a row to the tear-off gesture: a click calls `onTap`, a held press
 * released elsewhere calls `onTearOff` with the release point in screen
 * coordinates. Keyboard users get the tap on Enter/Space and the menu on the
 * context-menu key.
 */
export function useTearOff(
	onTap: () => void,
	onTearOff: (screenPoint: Point) => void,
	onMenu: (anchor: Point) => void,
): { readonly armed: boolean; readonly handlers: TearOffHandlers } {
	const gesture = useRef(new TearOffGesture())
	const timer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined)
	const origin = useRef<Point>({ x: 0, y: 0 })
	const [armed, setArmed] = useState(false)

	const stop = (): void => {
		clearTimeout(timer.current)
		timer.current = undefined
	}

	const handlers: TearOffHandlers = {
		onPointerDown: (e) => {
			if (e.button !== 0) return
			e.currentTarget.setPointerCapture(e.pointerId)
			origin.current = { x: e.clientX, y: e.clientY }
			gesture.current.press()
			timer.current = setTimeout(() => {
				gesture.current.hold()
				setArmed(gesture.current.armed)
			}, HOLD_MS)
		},
		onPointerMove: (e) => {
			gesture.current.move(e.clientX - origin.current.x, e.clientY - origin.current.y)
			if (!gesture.current.holding) stop()
		},
		onPointerUp: (e) => {
			stop()
			setArmed(false)
			const outcome = gesture.current.release()
			if (outcome === 'tap') onTap()
			else if (outcome === 'tear-off') onTearOff({ x: e.screenX, y: e.screenY })
		},
		onPointerCancel: () => {
			stop()
			setArmed(false)
			gesture.current.release()
		},
		onKeyDown: (e) => {
			if (e.key === 'Enter' || e.key === ' ') {
				e.preventDefault()
				onTap()
			}
		},
		onContextMenu: (e) => {
			e.preventDefault()
			onMenu({ x: e.clientX, y: e.clientY })
		},
	}
	return { armed, handlers }
}
