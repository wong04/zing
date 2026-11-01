import { useMemo, useRef, useState, type CSSProperties } from 'react'
import type { TabsState, TabView } from '@shared/ipc'
import { clampRailWidth } from '@shared/layout/chrome'
import { favourites, find, isFolder, pinned as pinnedItems } from '@shared/library/library'
import { buildSlots, snapshotOf } from '@shared/tabs/slots'
import { zing } from '../api'
import { PlusIcon } from '../chrome/icons'
import { commitMove } from '../chrome/tabs/commitMove'
import { commandTooltip } from '../chrome/tooltip'
import { GearIcon, GridIcon, SidebarIcon, WindowIcon } from '../components/icons'
import { useToolPanels } from '../panels/hooks'
import { PanelIcon } from '../panels/panelIcon'
import { useSettings } from '../settings/useSettings'
import { useLibraryState } from '../library/useLibraryState'
import type { DropTarget } from './dropTarget'
import { SplitGroup, StackGroup, TabRow, type RowFrame } from './RailRows'
import { FavouriteTiles, PinnedSection, type SavedFrame } from './SavedSections'
import { useItemDrag } from './useItemDrag'
import { useRowDrag } from './useRowDrag'
import './rail.css'

export type RailVariant = 'pinned' | 'floating'
export type RailSide = 'left' | 'right'

/**
 * The tabs on the window's side (Phase 4, docs/phase-4.md), as in Arc and Dia: the Favourites as tiles, the
 * pinned items in folders, New Tab, then today's tabs as rows, a site's stack as a group that folds. Favourites
 * and pinned items are saved tabs (SavedTabs on the host): each owns one tab, and closing it keeps the item.
 * Anything drags between the sections. On the left it is also the app sidebar (its
 * Toolbox, recent tools and Settings sit in its head), so there is one column rather than two. Pinned, it
 * is part of the chrome; hidden (Ctrl+S), the surface view draws it floating when the cursor reaches the edge.
 */
export function TabRail({ state, variant, side }: { state: TabsState | undefined; variant: RailVariant; side: RailSide }) {
	const handle = useSettings()
	const width = useLiveWidth(handle?.settings.verticalTabWidth)
	const tabs = useMemo(() => state?.tabs ?? [], [state?.tabs])
	const activeId = state?.activeId
	const organization = state?.organization ?? 'manual'
	const dropdowns = state?.dropdowns ?? false
	// A saved tab sits at its item, so today's tabs are the rest.
	const today = useMemo(() => tabs.filter((t) => t.itemId === undefined), [tabs])
	const loose = useMemo(() => buildSlots(today.map((t) => snapshotOf(t, t.id === activeId)), organization, dropdowns), [today, activeId, organization, dropdowns])
	const byId = useMemo(() => new Map<string, TabView>(tabs.map((t) => [t.id, t])), [tabs])
	const tabsByItem = useMemo(() => new Map<string, TabView>(tabs.flatMap((t) => (t.itemId === undefined ? [] : [[t.itemId, t] as const]))), [tabs])
	const library = useLibraryState()?.library
	const splitOf = useMemo(() => new Map((state?.splits ?? []).flatMap((s) => s.tabIds.map((id) => [id, s] as const))), [state?.splits])
	const pinnedCount = tabs.filter((t) => t.pinned).length
	const rowDrag = useRowDrag(
		loose.map((s) => s.key),
		(key, to, outside) => {
			const slot = loose.find((s) => s.key === key)
			if (!slot) return
			// Out of the list into Favourites or Pinned: the tab becomes a saved tab there.
			if (outside?.kind === 'folder') {
				if (slot.kind === 'tab') void zing.saveTab(slot.id, outside.folder, outside.at)
				return
			}
			void commitMove(slot, to, pinnedCount, dropdowns)
		},
	)
	const itemDrag = useItemDrag((id, target) => void dropItem(id, target, tabsByItem))
	const saved: SavedFrame = { tabsByItem, activeId, drag: itemDrag.handlersFor, endsDrag: itemDrag.consumeClick }
	const sidebarPinned = handle?.settings.sidebarPinned ?? true
	const hideLabel = sidebarPinned ? 'Hide sidebar (Ctrl+S)' : 'Keep sidebar open (Ctrl+S)'

	return (
		<nav className={`rail ${variant} ${side}`} aria-label="Tab rail" style={{ width: width.value } as CSSProperties}>
			<div className="rail-head">
				<div className="rail-drag" />
				{side === 'left' ? <AppButtons /> : null}
				<button type="button" className="rail-icon-button" aria-label={hideLabel} title={hideLabel} aria-pressed={sidebarPinned} onClick={() => handle?.set('sidebarPinned', !sidebarPinned)}>
					<SidebarIcon size={15} />
				</button>
			</div>
			{side === 'left' ? <RecentTools /> : null}
			{library ? <FavouriteTiles nodes={favourites(library)} frame={saved} /> : null}
			{library ? <PinnedSection nodes={pinnedItems(library)} frame={saved} /> : null}
			<button
				type="button"
				className="rail-row rail-new-tab"
				title={commandTooltip('tab.new')}
				onClick={() => void zing.runCommand('tab.new')}
				onContextMenu={(e) => {
					e.preventDefault()
					void zing.openStripMenu({ x: e.clientX, y: e.clientY })
				}}
			>
				<span className="rail-row-icon">
					<PlusIcon />
				</span>
				<span className="rail-row-title">New Tab</span>
			</button>
			<div className="rail-tabs" role="tablist" aria-label="Tabs" aria-orientation="vertical" data-drop="today" data-dragging={rowDrag.drag ? '' : undefined}>
				{loose.map((slot) => {
					const frame: RowFrame = {
						attach: rowDrag.register(slot.key),
						handlers: rowDrag.handlersFor(slot.key),
						offset: rowDrag.offsetOf(slot.key),
						dragging: rowDrag.drag?.key === slot.key,
						endsDrag: rowDrag.consumeClick,
					}
					if (slot.kind === 'tab') {
						const tab = byId.get(slot.id)
						if (!tab) return null
						// A split shows once, as a group, where its first tab among today's would be.
						const split = splitOf.get(tab.id)
						if (split) {
							const members = split.tabIds.flatMap((id) => today.find((t) => t.id === id) ?? [])
							return members[0]?.id === tab.id ? <SplitGroup key={split.id} tabs={members} activeId={activeId} /> : null
						}
						return <TabRow key={slot.key} tab={tab} active={tab.id === activeId} frame={frame} />
					}
					const members = slot.ids.flatMap((id) => byId.get(id) ?? [])
					return <StackGroup key={slot.key} name={slot.name} tabs={members} activeId={activeId} frame={frame} />
				})}
			</div>
			{variant === 'pinned' ? <ResizeEdge side={side} width={width} /> : null}
		</nav>
	)
}

/**
 * A saved item dropped somewhere in the rail: into a folder of Favourites or Pinned it moves there; among today's
 * tabs it stops being saved, and its tab (opened first if it has none) stays as an ordinary tab.
 */
async function dropItem(id: string, target: DropTarget, tabsByItem: ReadonlyMap<string, TabView>): Promise<void> {
	if (target.kind === 'folder') {
		if (target.folder !== id) await zing.applyLibrary({ type: 'moveInto', id, parent: target.folder, to: target.at })
		return
	}
	// A folder has no tab to keep, so it stays where it was.
	const node = find(id, (await zing.getLibrary()).library.bookmarks)
	if (!node || isFolder(node)) return
	if (!tabsByItem.has(id)) await zing.openSavedTab(id)
	await zing.applyLibrary({ type: 'remove', ids: [id] })
}

/** The app sidebar's own buttons, folded into the rail's head when tabs are on the left (T3). */
function AppButtons() {
	return (
		<>
			<button type="button" className="rail-icon-button" aria-label="Toolbox" title="Toolbox" onClick={() => void zing.openToolbox()}>
				<GridIcon size={15} />
			</button>
			<button type="button" className="rail-icon-button" aria-label="Settings" title="Settings" onClick={() => void zing.openSettings()}>
				<GearIcon size={15} />
			</button>
		</>
	)
}

/** Recent tools and minimized panels, as the app sidebar lists them, compact. */
function RecentTools() {
	const handle = useSettings()
	const panels = useToolPanels()
	const recents = handle?.settings.recentFeatures ?? []
	const minimized = panels.panels.filter((p) => p.minimized)
	if (recents.length === 0 && minimized.length === 0) return null
	return (
		<div className="rail-tools" aria-label="Recent tools">
			{recents.map((content) => (
				<button
					key={content}
					type="button"
					className="rail-icon-button"
					title={content}
					data-testid="recent-row"
					onClick={() => void zing.openPanel(content, { x: window.screenX + 560, y: window.screenY + 160 })}
				>
					<PanelIcon content={content} />
				</button>
			))}
			{minimized.map((panel) => (
				<button key={panel.id} type="button" className="rail-icon-button" title={`Restore ${panel.title}`} data-testid="minimized-row" onClick={() => void zing.restorePanel(panel.id)}>
					<WindowIcon size={12} />
				</button>
			))}
		</div>
	)
}

interface LiveWidth {
	readonly value: number
	readonly set: (width: number | undefined) => void
}

/** The rail's width: the setting, or the width under the pointer while its edge is dragged. */
function useLiveWidth(stored: number | undefined): LiveWidth {
	const [dragging, setDragging] = useState<number>()
	return { value: dragging ?? stored ?? 240, set: setDragging }
}

/** Drag the rail's inner edge to resize it; the page follows as the setting changes. */
function ResizeEdge({ side, width }: { side: RailSide; width: LiveWidth }) {
	const handle = useSettings()
	const start = useRef<{ x: number; width: number } | undefined>(undefined)
	const frame = useRef(0)
	const resize = (clientX: number): number | undefined => {
		const from = start.current
		if (!from) return undefined
		const dx = clientX - from.x
		return clampRailWidth(side === 'left' ? from.width + dx : from.width - dx)
	}
	return (
		<div
			className={`rail-resize ${side}`}
			role="separator"
			aria-orientation="vertical"
			aria-label="Resize tabs"
			onPointerDown={(e) => {
				if (e.button !== 0) return
				start.current = { x: e.clientX, width: width.value }
				e.currentTarget.setPointerCapture(e.pointerId)
			}}
			onPointerMove={(e) => {
				const next = resize(e.clientX)
				if (next === undefined || next === width.value) return
				width.set(next)
				// The page follows through the setting, at most once a frame.
				cancelAnimationFrame(frame.current)
				frame.current = requestAnimationFrame(() => handle?.set('verticalTabWidth', next))
			}}
			onPointerUp={(e) => {
				const next = resize(e.clientX)
				start.current = undefined
				if (next !== undefined) handle?.set('verticalTabWidth', next)
				width.set(undefined)
			}}
		/>
	)
}
