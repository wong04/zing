import { useLayoutEffect, useRef, type RefObject } from 'react'

/** Reports the element's size to the host while `shown`, and again whenever it changes: the host sizes the view to it. */
export function useReportedSize(report: (width: number, height: number) => Promise<void>, shown: boolean): RefObject<HTMLDivElement | null> {
	const root = useRef<HTMLDivElement>(null)
	useLayoutEffect(() => {
		const element = root.current
		if (!element || !shown) return
		const send = (): void => {
			const { width, height } = element.getBoundingClientRect()
			void report(width, height)
		}
		send()
		const observer = new ResizeObserver(send)
		observer.observe(element)
		return () => observer.disconnect()
	}, [report, shown])
	return root
}
