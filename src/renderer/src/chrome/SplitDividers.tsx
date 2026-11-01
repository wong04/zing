import { useRef, type CSSProperties } from 'react'
import type { ShownSplit, SplitView } from '@shared/ipc'
import { DIVIDER_PX, dragDivider, equalize } from '@shared/layout/split'
import { zing } from '../api'

/**
 * The dividers between a split's panes. The chrome's view lies under the pages, so the gap the host leaves
 * between two panes shows the chrome: the dividers are drawn there, and dragged there, with no overlay window.
 * Dragging one resizes its two neighbours (dragDivider); double-clicking makes every pane equal.
 */
export function SplitDividers({ split, shown }: { split: SplitView; shown: ShownSplit }) {
	const row = split.orientation === 'row'
	const first = shown.rects[0]
	const last = shown.rects.at(-1)
	if (!first || !last) return null
	const length = row ? last.x + last.width - first.x : last.y + last.height - first.y
	return (
		<>
			{shown.rects.slice(0, -1).map((pane, index) => {
				const style: CSSProperties = row
					? { left: pane.x + pane.width, top: pane.y, width: DIVIDER_PX, height: pane.height }
					: { left: pane.x, top: pane.y + pane.height, width: pane.width, height: DIVIDER_PX }
				return <Divider key={index} split={split} index={index} length={length} row={row} style={style} />
			})}
		</>
	)
}

function Divider({ split, index, length, row, style }: { split: SplitView; index: number; length: number; row: boolean; style: CSSProperties }) {
	// Where the drag began and the sizes then: each move is measured from there, not from the sizes the host sent back since.
	const start = useRef<{ at: number; ratios: number[] } | undefined>(undefined)
	const frame = useRef(0)
	const layout = { id: split.id, orientation: split.orientation, tabIds: [...split.tabIds], ratios: [...split.ratios] }
	const send = (ratios: number[]): void => {
		cancelAnimationFrame(frame.current)
		frame.current = requestAnimationFrame(() => void zing.setSplitRatios(split.id, ratios, length))
	}
	return (
		<div
			className={row ? 'split-divider row' : 'split-divider column'}
			role="separator"
			aria-orientation={row ? 'vertical' : 'horizontal'}
			aria-label="Resize split"
			style={style}
			onPointerDown={(e) => {
				if (e.button !== 0) return
				start.current = { at: row ? e.clientX : e.clientY, ratios: [...split.ratios] }
				e.currentTarget.setPointerCapture(e.pointerId)
			}}
			onPointerMove={(e) => {
				const from = start.current
				if (from === undefined) return
				const delta = (row ? e.clientX : e.clientY) - from.at
				send(dragDivider({ ...layout, ratios: from.ratios }, index, delta, length).ratios)
			}}
			onPointerUp={() => {
				start.current = undefined
			}}
			onDoubleClick={() => send(equalize(layout).ratios)}
		/>
	)
}
