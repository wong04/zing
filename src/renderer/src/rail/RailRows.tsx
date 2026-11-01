import { useState, type CSSProperties, type MouseEvent, type PointerEvent } from 'react'
import type { TabView } from '@shared/ipc'
import { zing } from '../api'
import { tabTitle, tabTooltip } from '../chrome/display'
import { CloseIcon } from '../chrome/icons'
import { TabIconView } from '../chrome/tabs/SlotChips'
import type { RowHandlers } from './useRowDrag'

const MIDDLE_BUTTON = 1

/** What a row needs from the rail: where to sit while a drag is under way, and how to start one. */
export interface RowFrame {
	readonly attach: (element: HTMLElement | null) => void
	readonly handlers: RowHandlers
	readonly offset: number
	readonly dragging: boolean
	readonly endsDrag: () => boolean
}

function rowStyle(frame: RowFrame): CSSProperties | undefined {
	return frame.offset === 0 ? undefined : { transform: `translateY(${frame.offset}px)` }
}

export function TabRow({ tab, active, frame, nested = false }: { tab: TabView; active: boolean; frame?: RowFrame; nested?: boolean }) {
	const activate = (): void => void zing.activateTab(tab.id)
	const onPointerDown = (e: PointerEvent<HTMLDivElement>): void => {
		frame?.handlers.onPointerDown(e)
		if (e.button === 0) activate()
	}
	const onAuxClick = (e: MouseEvent): void => {
		if (e.button === MIDDLE_BUTTON) void zing.closeTab(tab.id)
	}
	const onContextMenu = (e: MouseEvent): void => {
		e.preventDefault()
		void zing.openTabMenu(tab.id, { x: e.clientX, y: e.clientY })
	}
	const onClose = (e: MouseEvent): void => {
		e.stopPropagation()
		if (frame?.endsDrag()) return
		if (e.altKey) void zing.closeOtherTabs(tab.id)
		else void zing.closeTab(tab.id)
	}
	return (
		<div
			ref={(element) => frame?.attach(element)}
			role="tab"
			tabIndex={0}
			aria-selected={active}
			title={tabTooltip(tab)}
			data-tab-id={tab.id}
			data-page-state={tab.pageState}
			className={['rail-row', 'rail-tab', active && 'active', nested && 'nested', frame?.dragging && 'dragging'].filter(Boolean).join(' ')}
			style={frame ? rowStyle(frame) : undefined}
			onPointerDown={onPointerDown}
			onPointerMove={frame?.handlers.onPointerMove}
			onPointerUp={frame?.handlers.onPointerUp}
			onPointerCancel={frame?.handlers.onPointerCancel}
			onAuxClick={onAuxClick}
			onContextMenu={onContextMenu}
			onKeyDown={(e) => {
				if (e.key === 'Enter' || e.key === ' ') activate()
			}}
		>
			<span className="rail-row-icon">
				<TabIconView tab={tab} />
			</span>
			<span className="rail-row-title">{tabTitle(tab)}</span>
			<button type="button" className="rail-close" aria-label={`Close ${tabTitle(tab)}`} title="Close Tab" onPointerDown={(e) => e.stopPropagation()} onClick={onClose}>
				<CloseIcon />
			</button>
		</div>
	)
}

/** A split's tabs (split view) together in one box, in pane order; clicking one shows the split with that pane in front. */
export function SplitGroup({ tabs, activeId }: { tabs: readonly TabView[]; activeId: string | undefined }) {
	return (
		<div className="rail-split" role="group" aria-label="Split view">
			{tabs.map((tab) => (
				<TabRow key={tab.id} tab={tab} active={tab.id === activeId} />
			))}
		</div>
	)
}

/** A site's pages under one heading that folds; the heading moves the whole stack. */
export function StackGroup({ name, tabs, activeId, frame }: { name: string; tabs: readonly TabView[]; activeId: string | undefined; frame: RowFrame }) {
	const holdsActive = tabs.some((t) => t.id === activeId)
	const [open, setOpen] = useState(true)
	const lead = tabs.find((t) => t.id === activeId) ?? tabs[0]
	if (!lead) return null
	return (
		<div ref={(element) => frame.attach(element)} className={frame.dragging ? 'rail-group dragging' : 'rail-group'} style={rowStyle(frame)}>
			<div
				role="button"
				tabIndex={0}
				aria-expanded={open}
				className={holdsActive && !open ? 'rail-row rail-group-head active' : 'rail-row rail-group-head'}
				onPointerDown={frame.handlers.onPointerDown}
				onPointerMove={frame.handlers.onPointerMove}
				onPointerUp={frame.handlers.onPointerUp}
				onPointerCancel={frame.handlers.onPointerCancel}
				onClick={() => {
					if (!frame.endsDrag()) setOpen(!open)
				}}
			>
				<span className="rail-row-icon">
					<TabIconView tab={lead} />
				</span>
				<span className="rail-row-title">{name}</span>
				<span className="rail-group-count">{tabs.length}</span>
				<span className={open ? 'rail-chevron open' : 'rail-chevron'} aria-hidden="true">
					›
				</span>
			</div>
			{open ? tabs.map((tab) => <TabRow key={tab.id} tab={tab} active={tab.id === activeId} nested />) : null}
		</div>
	)
}
