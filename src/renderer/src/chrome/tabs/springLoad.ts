import { useEffect, useRef, type DragEvent } from 'react'

export interface SpringLoadHandlers {
	onDragEnter: (e: DragEvent<HTMLElement>) => void
	onDragLeave: (e: DragEvent<HTMLElement>) => void
}

/** How long a dragged file must rest over a tab before the tab reacts. */
export const SPRING_LOAD_MS = 600

/**
 * Spring-loading: something dragged in from outside (a file) and held over an
 * element fires `onFire` after a beat, the way a folder opens under a held
 * drag. `onPresence` reports whether the drag is over the element at all.
 * Tab reordering uses pointer events, so any HTML drag seen here is external.
 */
export function useSpringLoad(
	onFire: () => void,
	delay: number = SPRING_LOAD_MS,
	onPresence?: (inside: boolean) => void,
): SpringLoadHandlers {
	const timer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined)
	const latest = useRef({ onFire, onPresence })
	useEffect(() => {
		latest.current = { onFire, onPresence }
	})
	useEffect(() => () => clearTimeout(timer.current), [])

	return {
		onDragEnter: (e) => {
			if (e.currentTarget.contains(e.relatedTarget as Node | null)) return
			latest.current.onPresence?.(true)
			clearTimeout(timer.current)
			timer.current = setTimeout(() => latest.current.onFire(), delay)
		},
		onDragLeave: (e) => {
			if (e.currentTarget.contains(e.relatedTarget as Node | null)) return
			clearTimeout(timer.current)
			latest.current.onPresence?.(false)
		},
	}
}
