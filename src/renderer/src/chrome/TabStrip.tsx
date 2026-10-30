import { useCallback, useEffect, useMemo, useRef, useState, type MouseEvent } from 'react'
import { dividerWidth } from '@shared/browser/tabReorder'
import type { TabsState, TabView } from '@shared/ipc'
import { buildSlots, snapshotOf, type Slot } from '@shared/tabs/slots'
import { zing } from '../api'
import { PlusIcon } from './icons'
import { dragOffsetFor, keysAfterDrop } from './tabs/dragGeometry'
import { isPinnedSlot, slotHoldsTab, slotMetrics, TAB_GAP } from './tabs/layout'
import { StackChip, TabChip, type ChipFrame } from './tabs/SlotChips'
import { useSlotDrag, type Dropped } from './tabs/useSlotDrag'
import './tabs/tabs.css'
import { commandTooltip } from './tooltip'

/** The new-tab button and the gap before it, which the tabs cannot use. */
const NEW_TAB_RESERVE = 34
/** After a drop, how long the dropped order is held while main's new order travels back. */
const SETTLE_MS = 150

function useWidth(): [(element: HTMLElement | null) => void, number] {
	const [width, setWidth] = useState(0)
	const observer = useRef<ResizeObserver | undefined>(undefined)
	const ref = useCallback((element: HTMLElement | null): void => {
		observer.current?.disconnect()
		if (!element) return
		setWidth(element.clientWidth)
		observer.current = new ResizeObserver(() => setWidth(element.clientWidth))
		observer.current.observe(element)
	}, [])
	return [ref, width]
}

/** Slots in the dropped order until main's own order arrives, so a released chip never flashes back. */
function sortedBy(slots: readonly Slot[], keys: readonly string[] | undefined): readonly Slot[] {
	if (!keys) return slots
	const rank = (slot: Slot): number => {
		const at = keys.indexOf(slot.key)
		return at < 0 ? keys.length : at
	}
	return [...slots].sort((a, b) => rank(a) - rank(b))
}

async function commit(slot: Slot, to: number, pinnedCount: number, dropdowns: boolean): Promise<void> {
	if (slot.kind === 'stack') return zing.moveStack(slot.key, to)
	if (slot.pinned) return zing.moveTab(slot.id, to)
	if (dropdowns) return zing.moveStack(slot.key, to)
	return zing.moveTab(slot.id, pinnedCount + to)
}

export function TabStrip({ state }: { state: TabsState | undefined }) {
	const tabs = useMemo(() => state?.tabs ?? [], [state?.tabs])
	const activeId = state?.activeId
	const organization = state?.organization ?? 'manual'
	const dropdowns = state?.dropdowns ?? false
	const openStack = state?.openStack

	const [measure, roomWidth] = useWidth()
	const [heldKeys, setHeldKeys] = useState<readonly string[]>()

	const slots = useMemo(
		() =>
			buildSlots(
				tabs.map((t) => snapshotOf(t, t.id === activeId)),
				organization,
				dropdowns,
			),
		[tabs, activeId, organization, dropdowns],
	)
	const displayed = sortedBy(slots, heldKeys)
	const byId = useMemo(() => new Map<string, TabView>(tabs.map((t) => [t.id, t])), [tabs])
	const metrics = slotMetrics(displayed, activeId, Math.max(0, roomWidth - NEW_TAB_RESERVE))

	const onDrop = ({ slot, to, drag }: Dropped): void => {
		const section = keysAfterDrop(drag)
		const pinnedKeys = displayed.filter(isPinnedSlot).map((s) => s.key)
		const looseKeys = displayed.filter((s) => !isPinnedSlot(s)).map((s) => s.key)
		setHeldKeys(isPinnedSlot(slot) ? [...section, ...looseKeys] : [...pinnedKeys, ...section])
		const release = (): void => void setTimeout(() => setHeldKeys(undefined), SETTLE_MS)
		commit(slot, to, pinnedKeys.length, dropdowns).then(release, (error: unknown) => {
			setHeldKeys(undefined)
			throw error
		})
	}
	const { drag, register, handlersFor, consumeClick } = useSlotDrag(displayed, onDrop)

	// A click anywhere outside the open stack closes it, as with a menu.
	useEffect(() => {
		if (openStack === undefined) return
		const onPointerDown = (e: PointerEvent): void => {
			const chip = (e.target as Element | null)?.closest('[data-stack-key]')
			if (chip?.getAttribute('data-stack-key') === openStack) return
			void zing.closeStack()
		}
		document.addEventListener('pointerdown', onPointerDown, true)
		return () => document.removeEventListener('pointerdown', onPointerDown, true)
	}, [openStack])

	const frameFor = (slot: Slot): ChipFrame => ({
		width: metrics.get(slot.key)?.width ?? 0,
		compact: metrics.get(slot.key)?.compact ?? false,
		offset: drag ? dragOffsetFor(drag, slot.key) : 0,
		dragging: drag?.key === slot.key,
		attach: register(slot.key),
		drag: handlersFor(slot),
		endsDrag: consumeClick,
	})

	const pinned = displayed.filter(isPinnedSlot).length
	const onNewTabMenu = (e: MouseEvent): void => {
		e.preventDefault()
		void zing.openStripMenu({ x: e.clientX, y: e.clientY })
	}

	return (
		<div className="tabstrip">
			<div className="tab-room" ref={measure}>
				<div className="tabs" role="tablist" aria-label="Tabs" data-dragging={drag ? '' : undefined}>
					{displayed.map((slot, index) => (
						<SlotView
							key={slot.key}
							slot={slot}
							frame={frameFor(slot)}
							byId={byId}
							activeId={activeId}
							openStack={openStack}
							divider={pinned > 0 && index === pinned - 1 && pinned < displayed.length}
						/>
					))}
				</div>
				<button
					type="button"
					className="icon-button new-tab"
					aria-label="New Tab"
					title={commandTooltip('tab.new')}
					onClick={() => void zing.runCommand('tab.new')}
					onContextMenu={onNewTabMenu}
				>
					<PlusIcon />
				</button>
			</div>
		</div>
	)
}

interface SlotViewProps {
	readonly slot: Slot
	readonly frame: ChipFrame
	readonly byId: ReadonlyMap<string, TabView>
	readonly activeId: string | undefined
	readonly openStack: string | undefined
	/** The pinned run ends after this slot. */
	readonly divider: boolean
}

function SlotView({ slot, frame, byId, activeId, openStack, divider }: SlotViewProps) {
	const chip = chipFor(slot, frame, byId, activeId, openStack)
	if (!chip) return null
	return (
		<>
			{chip}
			{divider ? <div className="tab-divider" style={{ width: dividerWidth - TAB_GAP }} aria-hidden="true" /> : null}
		</>
	)
}

function chipFor(
	slot: Slot,
	frame: ChipFrame,
	byId: ReadonlyMap<string, TabView>,
	activeId: string | undefined,
	openStack: string | undefined,
) {
	if (slot.kind === 'tab') {
		const tab = byId.get(slot.id)
		return tab ? <TabChip tab={tab} active={slotHoldsTab(slot, activeId)} frame={frame} /> : null
	}
	const tabs = slot.ids.flatMap((id) => byId.get(id) ?? [])
	if (tabs.length === 0) return null
	return <StackChip stackKey={slot.key} name={slot.name} tabs={tabs} activeId={activeId} open={openStack === slot.key} frame={frame} />
}
