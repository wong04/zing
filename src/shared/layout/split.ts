// Split view: 2–4 tabs side by side in one window. A split is a layout over
// tab ids, not a special kind of tab, so sleeping, restore and ordering keep
// working on plain tabs.

export type SplitOrientation = 'row' | 'column'

export interface SplitLayout {
	id: string
	orientation: SplitOrientation
	tabIds: string[]
	// One per pane, summing to 1.
	ratios: number[]
}

export interface Rect {
	x: number
	y: number
	width: number
	height: number
}

export const MIN_PANES = 2
export const MAX_PANES = 4
export const DIVIDER_PX = 4
export const MIN_PANE_PX = 240

export class SplitError extends Error {
	override name = 'SplitError'
}

export function createSplit(tabIds: readonly string[], orientation: SplitOrientation): SplitLayout {
	assertPaneCount(tabIds.length)
	if (new Set(tabIds).size !== tabIds.length) throw new SplitError('A tab can only appear once in a split')
	return { id: crypto.randomUUID(), orientation, tabIds: [...tabIds], ratios: tabIds.map(() => 1 / tabIds.length) }
}

function assertPaneCount(count: number): void {
	if (count < MIN_PANES || count > MAX_PANES) throw new SplitError(`A split holds ${MIN_PANES}–${MAX_PANES} tabs, not ${count}`)
}

// The new pane takes an equal share; the others shrink proportionally.
export function addPane(layout: SplitLayout, tabId: string, at = layout.tabIds.length): SplitLayout {
	if (layout.tabIds.includes(tabId)) throw new SplitError(`Tab ${tabId} is already in this split`)
	assertPaneCount(layout.tabIds.length + 1)
	const share = 1 / (layout.tabIds.length + 1)
	const ratios = layout.ratios.map((r) => r * (1 - share))
	ratios.splice(at, 0, share)
	const tabIds = [...layout.tabIds]
	tabIds.splice(at, 0, tabId)
	return { ...layout, tabIds, ratios }
}

// Returns undefined when only one tab would be left: a split of one is just a tab.
export function removePane(layout: SplitLayout, tabId: string): SplitLayout | undefined {
	const index = layout.tabIds.indexOf(tabId)
	if (index === -1) throw new SplitError(`Tab ${tabId} is not in this split`)
	if (layout.tabIds.length - 1 < MIN_PANES) return undefined
	const tabIds = layout.tabIds.filter((id) => id !== tabId)
	const kept = layout.ratios.filter((_, i) => i !== index)
	const total = kept.reduce((a, b) => a + b, 0)
	return { ...layout, tabIds, ratios: kept.map((r) => r / total) }
}

// Whole device pixels, like Krackn's backingAlignedRect: a page laid out
// across half a pixel gets its whole composited layer resampled. The last
// pane absorbs the rounding so the panes always fill the container exactly.
export function paneRects(container: Rect, layout: SplitLayout, divider = DIVIDER_PX): Rect[] {
	const horizontal = layout.orientation === 'row'
	const origin = Math.round(horizontal ? container.x : container.y)
	const end = Math.round(horizontal ? container.x + container.width : container.y + container.height)
	const cross = horizontal ? { at: Math.round(container.y), size: Math.round(container.height) } : { at: Math.round(container.x), size: Math.round(container.width) }
	const usable = end - origin - divider * (layout.tabIds.length - 1)
	let cumulative = 0
	return layout.ratios.map((ratio, i) => {
		const start = origin + Math.round(cumulative * usable) + divider * i
		cumulative += ratio
		const stop = i === layout.ratios.length - 1 ? end : origin + Math.round(cumulative * usable) + divider * i
		const span = stop - start
		return horizontal ? { x: start, y: cross.at, width: span, height: cross.size } : { x: cross.at, y: start, width: cross.size, height: span }
	})
}

// Dragging divider `index` (between pane index and index+1) by `deltaPx`.
// Only the two neighbouring panes change, and neither goes below the minimum.
export function dragDivider(layout: SplitLayout, index: number, deltaPx: number, containerLength: number, minPanePx = MIN_PANE_PX, divider = DIVIDER_PX): SplitLayout {
	if (index < 0 || index >= layout.tabIds.length - 1) throw new SplitError(`No divider ${index} in a ${layout.tabIds.length}-pane split`)
	const usable = containerLength - divider * (layout.tabIds.length - 1)
	const before = layout.ratios[index]! * usable
	const after = layout.ratios[index + 1]! * usable
	const min = Math.min(minPanePx, (before + after) / 2)
	const moved = Math.max(min - before, Math.min(deltaPx, after - min))
	const ratios = [...layout.ratios]
	ratios[index] = (before + moved) / usable
	ratios[index + 1] = (after - moved) / usable
	return { ...layout, ratios }
}

export function equalize(layout: SplitLayout): SplitLayout {
	return { ...layout, ratios: layout.tabIds.map(() => 1 / layout.tabIds.length) }
}

export type StripUnit = { kind: 'tab'; tabId: string } | { kind: 'split'; splitId: string; tabIds: string[] }

// What the tab strip shows: each split once, as a joined chip, at the
// position of its first member. Ordering strategies treat a unit as one item,
// the way Krackn's TabOrganizer.movingStack treats a stack.
export function stripUnits(orderedTabIds: readonly string[], splits: readonly SplitLayout[]): StripUnit[] {
	const splitOf = new Map<string, SplitLayout>()
	for (const split of splits) for (const id of split.tabIds) splitOf.set(id, split)
	const emitted = new Set<string>()
	const units: StripUnit[] = []
	for (const tabId of orderedTabIds) {
		const split = splitOf.get(tabId)
		if (!split) {
			units.push({ kind: 'tab', tabId })
			continue
		}
		if (emitted.has(split.id)) continue
		emitted.add(split.id)
		units.push({ kind: 'split', splitId: split.id, tabIds: split.tabIds })
	}
	return units
}

// A tab dropped onto a page edge decides orientation and position.
export type DropEdge = 'left' | 'right' | 'top' | 'bottom'

export function splitFromDrop(targetTabId: string, droppedTabId: string, edge: DropEdge): SplitLayout {
	const orientation: SplitOrientation = edge === 'left' || edge === 'right' ? 'row' : 'column'
	const first = edge === 'left' || edge === 'top'
	return createSplit(first ? [droppedTabId, targetTabId] : [targetTabId, droppedTabId], orientation)
}
