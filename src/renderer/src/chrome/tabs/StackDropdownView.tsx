import { useEffect, useMemo, useRef, useState, type KeyboardEvent, type MouseEvent, type PointerEvent } from 'react'
import { moving } from '@shared/browser/tabOrganizer'
import { CHROME_LAYOUT, type StackDropdownState, type TabView } from '@shared/ipc'
import { zing } from '../../api'
import { tabTitle } from '../display'
import { tabIconContent } from '../TabIcon'
import { CloseIcon } from '../icons'
import { useSpringLoad } from './springLoad'

export const PANEL_WIDTH = 300
const PANEL_MARGIN = 8
const PANEL_GAP = 6
/** Row height plus the gap between rows. */
export const ROW_PITCH = 31
const DRAG_THRESHOLD = 4
/** Shorter than the strip's: a list is already a deliberate stop. */
const ROW_SPRING_MS = 350

function host(tab: TabView): string {
	return URL.canParse(tab.url) ? new URL(tab.url).host : ''
}

/** Where the row dragged `dy` pixels from its pick-up slot ends up. */
export function rowTarget(start: number, dy: number, count: number): number {
	return Math.min(Math.max(start + Math.round(dy / ROW_PITCH), 0), count - 1)
}

interface RowProps {
	readonly tab: TabView
	readonly current: boolean
	readonly highlighted: boolean
	readonly dragging: boolean
	readonly offset: number
	readonly onPick: () => void
	readonly onPointerDown: (e: PointerEvent<HTMLDivElement>) => void
	readonly onPointerMove: (e: PointerEvent<HTMLDivElement>) => void
	readonly onPointerUp: (e: PointerEvent<HTMLDivElement>) => void
}

function Row({ tab, current, highlighted, dragging, offset, onPick, onPointerDown, onPointerMove, onPointerUp }: RowProps) {
	// A file held over a page switches to it and gets out of the way, so the drop lands on the page.
	const springLoad = useSpringLoad(onPick, ROW_SPRING_MS)
	const onContextMenu = (e: MouseEvent): void => {
		e.preventDefault()
		// This view starts under the tab strip; menus are placed in window coordinates.
		void zing.openTabMenu(tab.id, { x: e.clientX, y: e.clientY + CHROME_LAYOUT.tabStripHeight })
	}
	const classes = ['stack-row', current && 'current', highlighted && 'highlighted', dragging && 'dragging'].filter(Boolean)
	return (
		<div
			role="menuitem"
			className={classes.join(' ')}
			style={{ transform: offset === 0 ? undefined : `translateY(${offset}px)` }}
			data-tab-id={tab.id}
			onPointerDown={onPointerDown}
			onPointerMove={onPointerMove}
			onPointerUp={onPointerUp}
			onContextMenu={onContextMenu}
			{...springLoad}
		>
			<span className={tab.loading ? 'tab-icon loading' : 'tab-icon'} aria-hidden="true">
				{tab.loading ? null : tabIconContent(tab)}
			</span>
			<span className="stack-row-text">
				<span className="stack-row-title">{tabTitle(tab)}</span>
				<span className="stack-row-host">{host(tab)}</span>
			</span>
			<button
				type="button"
				className="tab-close"
				aria-label={`Close ${tabTitle(tab)}`}
				onPointerDown={(e) => e.stopPropagation()}
				onClick={(e) => {
					e.stopPropagation()
					void zing.closeTab(tab.id)
				}}
			>
				<CloseIcon />
			</button>
		</div>
	)
}

interface RowDrag {
	readonly id: string
	readonly pointerId: number
	readonly startY: number
	readonly start: number
	started: boolean
}

function Panel({ state }: { state: StackDropdownState }) {
	const { tabs } = state
	const panel = useRef<HTMLDivElement>(null)
	const press = useRef<RowDrag | undefined>(undefined)
	const [picked, setPicked] = useState<{ key: string; id: string | undefined }>()
	const [drag, setDrag] = useState<{ id: string; start: number; target: number; dy: number }>()

	useEffect(() => {
		panel.current?.focus()
	}, [state.key])

	// Arrow keys move the highlight; until they do, it rests on the page you are on.
	const resting = tabs.some((t) => t.id === state.activeId) ? state.activeId : tabs[0]?.id
	const highlighted = picked?.key === state.key ? picked.id : resting
	const setHighlighted = (id: string | undefined): void => setPicked({ key: state.key, id })

	const ids = useMemo(() => tabs.map((t) => t.id), [tabs])
	const shown = useMemo(() => {
		if (!drag) return tabs
		const order = moving(drag.id, ids, drag.target)
		return order.flatMap((id) => tabs.filter((t) => t.id === id))
	}, [tabs, ids, drag])

	const pick = (id: string): void => {
		void zing.activateTab(id)
		void zing.closeStack()
	}

	const onKeyDown = (e: KeyboardEvent): void => {
		const at = Math.max(ids.indexOf(highlighted ?? ''), 0)
		if (e.key === 'Escape') void zing.closeStack()
		else if (e.key === 'ArrowDown') setHighlighted(ids[Math.min(at + 1, ids.length - 1)])
		else if (e.key === 'ArrowUp') setHighlighted(ids[Math.max(at - 1, 0)])
		else if (e.key === 'Enter' && highlighted) pick(highlighted)
		else return
		e.preventDefault()
	}

	const down = (id: string) => (e: PointerEvent<HTMLDivElement>) => {
		if (e.button !== 0) return
		press.current = { id, pointerId: e.pointerId, startY: e.clientY, start: ids.indexOf(id), started: false }
	}
	const move = (e: PointerEvent<HTMLDivElement>): void => {
		const p = press.current
		if (!p || p.pointerId !== e.pointerId) return
		const dy = e.clientY - p.startY
		if (!p.started) {
			if (Math.abs(dy) < DRAG_THRESHOLD) return
			p.started = true
			e.currentTarget.setPointerCapture(e.pointerId)
		}
		setDrag({ id: p.id, start: p.start, target: rowTarget(p.start, dy, ids.length), dy })
	}
	const up = (e: PointerEvent<HTMLDivElement>): void => {
		const p = press.current
		if (!p || p.pointerId !== e.pointerId) return
		press.current = undefined
		if (!p.started) {
			pick(p.id)
			return
		}
		e.currentTarget.releasePointerCapture(e.pointerId)
		const done = drag
		setDrag(undefined)
		if (done && done.target !== done.start) void zing.moveWithinStack(done.id, state.key, done.target)
	}

	const left = Math.min(Math.max(state.anchorX - PANEL_WIDTH / 2, PANEL_MARGIN), window.innerWidth - PANEL_WIDTH - PANEL_MARGIN)
	const height = Math.min(tabs.length * ROW_PITCH - 1, 360)

	return (
		<div
			ref={panel}
			className="stack-panel"
			role="menu"
			aria-label={`${state.name}, ${tabs.length} tabs`}
			tabIndex={-1}
			style={{ left, top: PANEL_GAP, width: PANEL_WIDTH }}
			onKeyDown={onKeyDown}
			onPointerDown={(e) => e.stopPropagation()}
			onDragEnter={() => void zing.stackDragPresence('panel', true)}
			onDragLeave={(e) => {
				if (!e.currentTarget.contains(e.relatedTarget as Node | null)) void zing.stackDragPresence('panel', false)
			}}
		>
			<div className="stack-header">
				<span className="stack-name">{state.name}</span>
				<span className="stack-total">{tabs.length} tabs</span>
			</div>
			<div className="stack-rows" style={{ height }}>
				{shown.map((tab) => {
					const dragging = drag?.id === tab.id
					const offset = dragging && drag ? drag.dy - (drag.target - drag.start) * ROW_PITCH : 0
					return (
						<Row
							key={tab.id}
							tab={tab}
							current={tab.id === state.activeId}
							highlighted={tab.id === highlighted || dragging}
							dragging={dragging}
							offset={offset}
							onPick={() => pick(tab.id)}
							onPointerDown={down(tab.id)}
							onPointerMove={move}
							onPointerUp={up}
						/>
					)
				})}
			</div>
			<div className="stack-rule" />
			<button
				type="button"
				className="stack-action"
				onClick={() => {
					tabs.forEach((t) => void zing.closeTab(t.id))
					void zing.closeStack()
				}}
			>
				Close All {tabs.length} Tabs
			</button>
		</div>
	)
}

/** The dropdown view's whole content: a backdrop that closes on click, and the stack's page list. */
export function StackDropdownView() {
	const [state, setState] = useState<StackDropdownState | null>(null)
	useEffect(() => {
		const unsubscribe = zing.onStackDropdown(setState)
		// The first push can land before this view has mounted; ask for it.
		void zing.currentStack().then((current) => setState((previous) => previous ?? current))
		return unsubscribe
	}, [])
	if (!state) return null
	return (
		<div className="stack-backdrop" onPointerDown={() => void zing.closeStack()}>
			<Panel state={state} />
		</div>
	)
}
