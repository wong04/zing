import { pinnedWidth, tabBarLayout } from '@shared/browser/tabReorder'
import type { Slot } from '@shared/tabs/slots'

/** Krackn lets one tab span the window; on a wide screen that is a very long chip, so it stops here. */
export const MAX_TAB_WIDTH = 240
/** Gap between chips, matching `tabBarLayout`'s default spacing. */
export const TAB_GAP = 2

export interface SlotMetrics {
	readonly width: number
	/** Icon only: pinned tabs always, loose tabs when the row is too crowded for titles. */
	readonly compact: boolean
}

export function isPinnedSlot(slot: Slot): boolean {
	return slot.kind === 'tab' && slot.pinned
}

export function slotHoldsTab(slot: Slot, id: string | undefined): boolean {
	if (id === undefined) return false
	return slot.kind === 'tab' ? slot.id === id : slot.ids.includes(id)
}

/** Width of every slot by key, from the shared Safari-style rule in `tabBarLayout`. */
export function slotMetrics(slots: readonly Slot[], activeId: string | undefined, total: number): Map<string, SlotMetrics> {
	const metrics = new Map<string, SlotMetrics>()
	const loose = slots.filter((s) => !isPinnedSlot(s))
	for (const slot of slots) if (isPinnedSlot(slot)) metrics.set(slot.key, { width: pinnedWidth, compact: true })
	const activeIndex = loose.findIndex((s) => slotHoldsTab(s, activeId))
	const layout = tabBarLayout({
		total,
		pinned: slots.length - loose.length,
		loose: loose.length,
		activeLoose: activeIndex < 0 ? null : activeIndex,
		spacing: TAB_GAP,
	})
	loose.forEach((slot, i) => {
		metrics.set(slot.key, {
			width: Math.min(layout.looseWidths[i] ?? MAX_TAB_WIDTH, MAX_TAB_WIDTH),
			compact: layout.compact[i] ?? false,
		})
	})
	return metrics
}
