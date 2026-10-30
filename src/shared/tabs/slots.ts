import { registrableDomain } from '../browser/registrableDomain'
import { siteGroupOfURL } from '../browser/siteGroup'
import { displayedStacks, makeTabSnapshot, type Stack, type TabOrganization, type TabSnapshot } from '../browser/tabOrganizer'
import type { TabRecord } from '../model/tab'

export type SnapshotSource = Pick<TabRecord, 'id' | 'url' | 'title' | 'pinned' | 'createdAt' | 'lastActiveAt'>

/** `BrowserTab.snapshot`: a tab with no http(s) site yet (a blank tab) has an empty domain and group. */
export function snapshotOf(tab: SnapshotSource, isActive: boolean): TabSnapshot {
	const base = {
		id: tab.id,
		title: tab.title,
		lastAccessed: tab.lastActiveAt,
		createdAt: tab.createdAt,
		isPinned: tab.pinned,
		isActive,
	}
	if (!URL.canParse(tab.url)) return makeTabSnapshot({ ...base, domain: '' })
	const url = new URL(tab.url)
	if (url.hostname === '') return makeTabSnapshot({ ...base, domain: '' })
	const group = siteGroupOfURL(url)
	return makeTabSnapshot({
		...base,
		domain: registrableDomain(url.hostname),
		group: group.key,
		groupName: group.name,
	})
}

/** One position in the strip: a single tab, or a stack standing for several pages of a site. */
export interface TabSlot {
	readonly kind: 'tab'
	readonly key: string
	readonly id: string
	readonly pinned: boolean
}

export interface StackSlot {
	readonly kind: 'stack'
	readonly key: string
	readonly name: string
	readonly ids: readonly string[]
}

export type Slot = TabSlot | StackSlot

export function pinnedSlotKey(id: string): string {
	return `pin:${id}`
}

/**
 * What the strip draws, in order. Separate: every tab is a slot. Dropdown:
 * pinned tabs stay single slots, and the rest are the arrangement's stacks,
 * where a one-page site is just a tab.
 */
export function buildSlots(snapshots: readonly TabSnapshot[], organization: TabOrganization, dropdowns: boolean): Slot[] {
	if (!dropdowns) {
		return snapshots.map((s) => ({
			kind: 'tab',
			key: s.isPinned ? pinnedSlotKey(s.id) : s.id,
			id: s.id,
			pinned: s.isPinned,
		}))
	}
	const pinned = snapshots.filter((s) => s.isPinned)
	const loose = displayedStacks(snapshots, organization).map((stack): Slot => slotOfStack(stack))
	return [...pinned.map((s): Slot => ({ kind: 'tab', key: pinnedSlotKey(s.id), id: s.id, pinned: true })), ...loose]
}

function slotOfStack(stack: Stack): Slot {
	const [only, ...rest] = stack.ids
	if (only !== undefined && rest.length === 0) return { kind: 'tab', key: stack.key, id: only, pinned: false }
	return { kind: 'stack', key: stack.key, name: stack.name, ids: stack.ids }
}

/** The multi-page stack with this key under the current arrangement. */
export function findStack(snapshots: readonly TabSnapshot[], organization: TabOrganization, key: string): Stack | undefined {
	return displayedStacks(snapshots, organization).find((stack) => stack.key === key && stack.ids.length > 1)
}

export function pinnedSlotCount(slots: readonly Slot[]): number {
	return slots.filter((slot) => slot.kind === 'tab' && slot.pinned).length
}

