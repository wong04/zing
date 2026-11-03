import { createElement, Suspense, useEffect, useRef, useState, type PointerEvent, type RefObject } from 'react'
import { panelContentFromId } from '@shared/toolbox/panelContent'
import type { Point, ToolPanelView, ToolPanelsState } from '@shared/shell/shellState'
import { zing } from '../api'
import { ToolEmptyState } from '../components'
import { CloseGlyph, MinusIcon } from '../components/icons'
import { PanelInputContext } from '../tools/panelInput'
import { toolComponent } from '../tools/registry'
import { PanelErrorBoundary } from './PanelErrorBoundary'
import { PanelIcon } from './panelIcon'
import './panels.css'

function EmptyBody({ panel }: { panel: ToolPanelView }) {
	return (
		<ToolEmptyState
			icon={<PanelIcon content={panel.content} size={44} />}
			title={panel.title}
			message="This tool arrives in a later phase. The panel is the real window it will live in."
		/>
	)
}

function PanelBody({ panel }: { panel: ToolPanelView }) {
	const content = panelContentFromId(panel.content)
	const Body = content ? toolComponent(content) : null
	if (!Body) return <EmptyBody panel={panel} />
	return (
		<PanelInputContext value={panel.input ?? null}>
			<Suspense fallback={null}>{createElement(Body)}</Suspense>
		</PanelInputContext>
	)
}

const fieldSelector = 'input:not([type="hidden"]):not([disabled]), textarea:not([disabled]), select:not([disabled])'

/**
 * Puts the keyboard in the panel's first field when the panel opens or is restored, so a tool picked from the
 * Toolbox is usable at once. Tool bodies load lazily, so this waits for the first field to appear, for a few seconds at most.
 */
function useFocusFirstField(body: RefObject<HTMLElement | null>, active: boolean): void {
	useEffect(() => {
		const element = body.current
		if (!active || !element) return
		const focusFirst = (): boolean => {
			const field = element.querySelector<HTMLElement>(fieldSelector)
			if (!field) return false
			field.focus()
			return true
		}
		if (focusFirst()) return
		const observer = new MutationObserver(() => {
			if (focusFirst()) observer.disconnect()
		})
		observer.observe(element, { childList: true, subtree: true })
		const giveUp = setTimeout(() => observer.disconnect(), 3000)
		return () => {
			observer.disconnect()
			clearTimeout(giveUp)
		}
	}, [body, active])
}

function PanelWindow({ panel, front, order, origin }: { panel: ToolPanelView; front: boolean; order: number; origin: Point }) {
	// Where the pointer went since the grab, in screen pixels. The panel follows it locally and
	// main hears only the drop, so the panels it pushes aside don't animate while it is held.
	const [drag, setDrag] = useState<Point | null>(null)
	const [grab, setGrab] = useState<Point | null>(null)
	const body = useRef<HTMLDivElement>(null)
	useFocusFirstField(body, !panel.minimized)

	const x = panel.frame.x + (drag?.x ?? 0) - origin.x
	const y = panel.frame.y + (drag?.y ?? 0) - origin.y

	const onDown = (e: PointerEvent): void => {
		if (e.button !== 0 || (e.target as HTMLElement).closest('button')) return
		e.currentTarget.setPointerCapture(e.pointerId)
		setGrab({ x: e.screenX, y: e.screenY })
		setDrag({ x: 0, y: 0 })
		void zing.beginPanelDrag(panel.id)
	}
	const onMove = (e: PointerEvent): void => {
		if (!grab) return
		setDrag({ x: e.screenX - grab.x, y: e.screenY - grab.y })
	}
	const onUp = (e: PointerEvent): void => {
		if (!grab) return
		const dx = e.screenX - grab.x
		const dy = e.screenY - grab.y
		setGrab(null)
		setDrag(null)
		void zing.endPanelDrag(panel.id, { x: panel.frame.x + dx, y: panel.frame.y + dy })
	}

	return (
		<section
			role="dialog"
			aria-label={panel.title}
			className={`float-panel${front ? ' front' : ''}${grab ? ' dragging' : ''}`}
			data-testid="float-panel"
			hidden={panel.minimized}
			style={{ left: x, top: y, width: panel.frame.width, height: panel.frame.height, zIndex: order }}
			onPointerDown={() => {
				if (!front) void zing.focusPanel(panel.id)
			}}
		>
			<header
				className="float-title"
				onPointerDown={onDown}
				onPointerMove={onMove}
				onPointerUp={onUp}
				onPointerCancel={onUp}
				onDoubleClick={() => void zing.minimizePanel(panel.id)}
			>
				<span className="float-title-icon">
					<PanelIcon content={panel.content} size={14} />
				</span>
				<span className="float-title-text">{panel.title}</span>
				<span className="float-controls">
					<button type="button" aria-label={`Minimize ${panel.title} to the sidebar`} title="Minimize to sidebar" onClick={() => void zing.minimizePanel(panel.id)}>
						<MinusIcon size={12} />
					</button>
					<button type="button" aria-label={`Close ${panel.title}`} title="Close" onClick={() => void zing.closePanel(panel.id)}>
						<CloseGlyph size={12} />
					</button>
				</span>
			</header>
			<div className="float-body" ref={body}>
				<PanelErrorBoundary>
					<PanelBody panel={panel} />
				</PanelErrorBoundary>
			</div>
		</section>
	)
}

/**
 * Floating tool panels drawn over the page in the surface view (Krackn `FloatingPanelView`).
 * The host reorders the list to raise a panel, but React must not follow it: moving a panel's DOM node blurs
 * its field and resets its scroll. So the nodes stay in id order and the stacking comes from `zIndex` alone.
 */
export function PanelHost({ state, origin }: { state: ToolPanelsState; origin: Point }) {
	const stacking = new Map(state.panels.map((panel, index) => [panel.id, index + 1]))
	const stable = [...state.panels].sort((a, b) => (a.id < b.id ? -1 : a.id > b.id ? 1 : 0))
	return (
		<>
			{stable.map((panel) => (
				<PanelWindow key={panel.id} panel={panel} front={panel.id === state.frontId} order={stacking.get(panel.id) ?? 1} origin={origin} />
			))}
		</>
	)
}
