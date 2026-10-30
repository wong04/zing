import { useCallback, useEffect, useRef, useState, type CSSProperties, type ReactNode } from 'react'
import { scrollEdges, type ScrollEdges } from './scrollEdges'

export interface FadingScrollProps {
	readonly children: ReactNode
	/** Height of each frosted edge, in px. Krackn used 40 for pages and 22 for lists. */
	readonly fadeHeight?: number
	/** Cap the viewport; the area scrolls past it. Omit to fill the parent. */
	readonly maxHeight?: number
	readonly className?: string
	readonly 'aria-label'?: string
}

const NONE: ScrollEdges = { top: false, bottom: false }

/**
 * A scroll area whose top and bottom edges frost over when there is more
 * content past them (Krackn `FadingScrollView` / `.scrollEdgeFade`).
 */
export function FadingScroll({ children, fadeHeight = 40, maxHeight, className, 'aria-label': label }: FadingScrollProps) {
	const body = useRef<HTMLDivElement>(null)
	const [edges, setEdges] = useState<ScrollEdges>(NONE)

	const measure = useCallback((): void => {
		const el = body.current
		if (!el) return
		const next = scrollEdges({ offset: el.scrollTop, contentHeight: el.scrollHeight, viewportHeight: el.clientHeight })
		setEdges((prev) => (prev.top === next.top && prev.bottom === next.bottom ? prev : next))
	}, [])

	useEffect(() => {
		const el = body.current
		if (!el) return
		measure()
		const observer = new ResizeObserver(measure)
		observer.observe(el)
		for (const child of el.children) observer.observe(child)
		return () => observer.disconnect()
	}, [measure])

	const style = { '--fade-height': `${fadeHeight}px`, maxHeight } as CSSProperties
	return (
		<div className={className ? `fading-scroll ${className}` : 'fading-scroll'} style={style}>
			<div ref={body} className="fading-scroll-body" onScroll={measure} tabIndex={-1} role="region" aria-label={label}>
				{children}
			</div>
			<div className="fade-edge top" data-visible={edges.top} aria-hidden="true" />
			<div className="fade-edge bottom" data-visible={edges.bottom} aria-hidden="true" />
		</div>
	)
}
