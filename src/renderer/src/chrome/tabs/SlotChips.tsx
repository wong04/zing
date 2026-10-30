import { useRef, type CSSProperties, type KeyboardEvent, type MouseEvent, type PointerEvent } from 'react'
import type { TabView } from '@shared/ipc'
import { zing } from '../../api'
import { tabTitle } from '../display'
import { tabIconContent } from '../TabIcon'
import { CloseIcon } from '../icons'
import { useSpringLoad } from './springLoad'
import type { SlotDragHandlers } from './useSlotDrag'

const MIDDLE_BUTTON = 1

/** What every chip needs from the strip: where to sit, whether it is being dragged, how to start a drag. */
export interface ChipFrame {
	readonly width: number
	readonly compact: boolean
	readonly offset: number
	readonly dragging: boolean
	readonly attach: (element: HTMLDivElement | null) => void
	readonly drag: SlotDragHandlers
	/** True for the click that ends a drag, which must not also select or open. */
	readonly endsDrag: () => boolean
}

function chipStyle({ width, offset }: ChipFrame): CSSProperties {
	return { width, transform: offset === 0 ? undefined : `translateX(${offset}px)` }
}

function classes(...names: (string | false)[]): string {
	return names.filter(Boolean).join(' ')
}

function Icon({ tab }: { tab: TabView }) {
	return (
		<span className={tab.loading ? 'tab-icon loading' : 'tab-icon'} aria-hidden="true">
			{tab.loading ? null : tabIconContent(tab)}
		</span>
	)
}

export function TabChip({ tab, active, frame }: { tab: TabView; active: boolean; frame: ChipFrame }) {
	const springLoad = useSpringLoad(() => void zing.activateTab(tab.id))
	const title = tabTitle(tab)
	const iconOnly = tab.pinned || frame.compact

	const activate = (): void => void zing.activateTab(tab.id)
	const close = (): void => void zing.closeTab(tab.id)

	const onPointerDown = (e: PointerEvent<HTMLDivElement>): void => {
		frame.drag.onPointerDown(e)
		if (e.button === 0) activate()
	}
	// Pinned tabs are not closed by accident; the menu's Close Tab is disabled for them too.
	const onAuxClick = (e: MouseEvent): void => {
		if (e.button === MIDDLE_BUTTON && !tab.pinned) close()
	}
	const onKeyDown = (e: KeyboardEvent): void => {
		if (e.key === 'Enter' || e.key === ' ') activate()
	}
	const onContextMenu = (e: MouseEvent): void => {
		e.preventDefault()
		void zing.openTabMenu(tab.id, { x: e.clientX, y: e.clientY })
	}
	// Alt-click closes every other tab, as in Krackn.
	const onClose = (e: MouseEvent): void => {
		e.stopPropagation()
		if (e.altKey) void zing.closeOtherTabs(tab.id)
		else close()
	}

	return (
		<div
			ref={(element) => frame.attach(element)}
			role="tab"
			aria-selected={active}
			tabIndex={active ? 0 : -1}
			className={classes('tab', active && 'active', tab.pinned && 'pinned', iconOnly && 'icon-only', frame.dragging && 'dragging', tab.sleeping === true && 'sleeping')}
			style={chipStyle(frame)}
			title={title}
			data-tab-id={tab.id}
			data-pinned={tab.pinned}
			data-sleeping={tab.sleeping ? 'true' : undefined}
			onPointerDown={onPointerDown}
			onPointerMove={frame.drag.onPointerMove}
			onPointerUp={frame.drag.onPointerUp}
			onPointerCancel={frame.drag.onPointerCancel}
			onAuxClick={onAuxClick}
			onKeyDown={onKeyDown}
			onContextMenu={onContextMenu}
			{...springLoad}
		>
			{/* Close sits on the left, as in Krackn, so it never moves when a title changes width. */}
			{tab.pinned ? null : (
				<button
					type="button"
					className="tab-close"
					aria-label={`Close ${title}`}
					onPointerDown={(e) => e.stopPropagation()}
					onClick={onClose}
				>
					<CloseIcon />
				</button>
			)}
			<Icon tab={tab} />
			{iconOnly ? null : <span className="tab-title">{title}</span>}
		</div>
	)
}

export interface StackChipProps {
	readonly stackKey: string
	readonly name: string
	readonly tabs: readonly TabView[]
	readonly activeId: string | undefined
	readonly open: boolean
	readonly frame: ChipFrame
}

function centreX(element: HTMLElement): number {
	const rect = element.getBoundingClientRect()
	return rect.left + rect.width / 2
}

/** One slot standing for every open page of a site; clicking it lists them in the overlay's dropdown. */
export function StackChip({ stackKey, name, tabs, activeId, open, frame }: StackChipProps) {
	const dragCentre = useRef(0)
	const springLoad = useSpringLoad(
		() => void zing.openStackForDrag(stackKey, dragCentre.current),
		undefined,
		(inside) => void zing.stackDragPresence('chip', inside),
	)
	const current = tabs.find((t) => t.id === activeId)
	const lead = current ?? tabs[0]!

	const toggle = (element: HTMLElement): void => {
		if (frame.endsDrag()) return
		void zing.toggleStack(stackKey, centreX(element))
	}
	const onKeyDown = (e: KeyboardEvent<HTMLDivElement>): void => {
		if (e.key === 'Enter' || e.key === ' ') toggle(e.currentTarget)
	}
	const onContextMenu = (e: MouseEvent): void => {
		e.preventDefault()
		void zing.openStackMenu(stackKey, { x: e.clientX, y: e.clientY })
	}
	const active = current !== undefined

	return (
		<div
			ref={(element) => frame.attach(element)}
			role="tab"
			aria-selected={active}
			aria-haspopup="menu"
			aria-expanded={open}
			tabIndex={active ? 0 : -1}
			className={classes('tab', 'stack', active && 'active', open && 'open', frame.compact && 'icon-only', frame.dragging && 'dragging')}
			style={chipStyle(frame)}
			title={`${tabs.length} tabs from ${name}`}
			data-stack-key={stackKey}
			onPointerDown={frame.drag.onPointerDown}
			onPointerMove={frame.drag.onPointerMove}
			onPointerUp={frame.drag.onPointerUp}
			onPointerCancel={frame.drag.onPointerCancel}
			onClick={(e) => toggle(e.currentTarget)}
			onKeyDown={onKeyDown}
			onContextMenu={onContextMenu}
			onDragEnter={(e) => {
				dragCentre.current = centreX(e.currentTarget)
				springLoad.onDragEnter(e)
			}}
			onDragLeave={springLoad.onDragLeave}
		>
			<Icon tab={lead} />
			{frame.compact ? null : <span className="tab-title">{active ? tabTitle(lead) : name}</span>}
			<span className="stack-count" aria-label={`${tabs.length} tabs`}>
				{tabs.length}
			</span>
		</div>
	)
}
