// Port of Krackn's `TabSnapshot`, `TabOrganization` and `TabOrganizer`
// (Features/Browser/TabOrganizer.swift), plus `BrowserModel.arrange` / `freezeDisplayedOrder`.

/**
 * Everything the organiser and the auto-closer need to know about a tab,
 * without dragging a web view along. Splitting this out is what makes both
 * policies testable — otherwise checking "does a 7-day rule spare a tab
 * touched an hour ago" would mean standing up a real web view.
 */
export interface TabSnapshot {
	id: string
	title: string
	/** Registrable domain, e.g. `google.com` for `docs.google.com`. */
	domain: string
	/**
	 * Which dropdown the tab joins, and what that dropdown is called — the
	 * domain, except where one domain is many products (`siteGroupOf`).
	 */
	group: string
	groupName: string
	/** Epoch milliseconds. */
	lastAccessed: number
	/** Epoch milliseconds. */
	createdAt: number
	isPinned: boolean
	isActive: boolean
}

export interface TabSnapshotInit {
	id: string
	title: string
	domain: string
	group?: string
	groupName?: string
	lastAccessed: number
	createdAt: number
	isPinned?: boolean
	isActive?: boolean
}

export function makeTabSnapshot(init: TabSnapshotInit): TabSnapshot {
	return {
		id: init.id,
		title: init.title,
		domain: init.domain,
		group: init.group ?? init.domain,
		groupName: init.groupName ?? init.group ?? init.domain,
		lastAccessed: init.lastAccessed,
		createdAt: init.createdAt,
		isPinned: init.isPinned ?? false,
		isActive: init.isActive ?? false,
	}
}

/** How the tab strip keeps itself in order. */
export const tabOrganizations = ['manual', 'domain', 'alphabetical', 'recentlyUsed', 'newest'] as const
export type TabOrganization = (typeof tabOrganizations)[number]

export const tabOrganizationLabel: Readonly<Record<TabOrganization, string>> = {
	manual: 'Manual',
	domain: 'By site',
	alphabetical: 'A–Z',
	recentlyUsed: 'Last opened',
	newest: 'Newest first',
}

export const tabOrganizationDetail: Readonly<Record<TabOrganization, string>> = {
	manual: 'Tabs stay wherever you drag them.',
	domain: 'Tabs from the same site stay together. Drag one to move its site.',
	alphabetical: 'Sorted by page title.',
	recentlyUsed: 'Most recently viewed first.',
	newest: 'Most recently opened first.',
}

/**
 * What dragging a tab does under each strategy.
 *
 * Manual: it goes where you drop it. By site: tabs stay with their site,
 * but grouping follows the order sites first appear — so dragging a tab
 * moves its whole site's group, and dragging within a group reorders it.
 * The sorted strategies have no room for a hand-placed tab, so a drag
 * there switches the strip to manual (and says so) rather than snapping
 * back, which reads as broken.
 */
export const dragSwitchesToManual: Readonly<Record<TabOrganization, boolean>> = {
	manual: false,
	domain: false,
	alphabetical: true,
	recentlyUsed: true,
	newest: true,
}

// Case- and diacritic-insensitive so "iPhone" and "Álvaro" land where a person would look for them.
const titleCollator = new Intl.Collator(undefined, { sensitivity: 'base' })
// Swift's `localizedCaseInsensitiveCompare`: case folds, accents still count.
const nameCollator = new Intl.Collator(undefined, { sensitivity: 'accent' })

function sortLoose(loose: TabSnapshot[], strategy: TabOrganization): TabSnapshot[] {
	switch (strategy) {
		case 'manual':
			return loose
		case 'alphabetical':
			return [...loose].sort((a, b) => titleCollator.compare(a.title, b.title))
		case 'recentlyUsed':
			return [...loose].sort((a, b) => b.lastAccessed - a.lastAccessed)
		case 'newest':
			return [...loose].sort((a, b) => b.createdAt - a.createdAt)
		case 'domain':
			return groupByDomain(loose)
	}
}

/**
 * Returns the ids in the order the strip should show them.
 *
 * Pinned tabs are never touched: they hold their relative order and stay
 * at the front, which is the whole point of pinning. Only the unpinned
 * remainder is sorted.
 */
export function order(tabs: readonly TabSnapshot[], strategy: TabOrganization): string[] {
	const pinned = tabs.filter((tab) => tab.isPinned)
	const loose = tabs.filter((tab) => !tab.isPinned)
	return [...pinned, ...sortLoose(loose, strategy)].map((tab) => tab.id)
}

/**
 * Clumps same-site tabs together while keeping the strip predictable:
 * sites appear in the order you first opened one, and within a site the
 * tabs stay in the order you opened them. Sorting the groups
 * alphabetically instead would make an existing tab jump across the strip
 * whenever an unrelated site was opened.
 */
function groupByDomain(tabs: readonly TabSnapshot[]): TabSnapshot[] {
	const groups = new Map<string, TabSnapshot[]>()
	for (const tab of tabs) {
		// Tabs with no domain yet — a blank new tab — are their own bucket
		// so they don't all pile under the first real site.
		const key = tab.domain === '' ? '￿new' : tab.domain
		const group = groups.get(key)
		if (group) group.push(tab)
		else groups.set(key, [tab])
	}
	return [...groups.values()].flat()
}

/**
 * One entry per site, for the Dropdown tab style: the strip shows a
 * single tab for each site, and clicking it lists that site's pages.
 *
 * A tab with no site yet (a new tab) is its own entry, and pinned tabs
 * aren't grouped; they stay as icons at the left.
 */
export interface Stack {
	/** The group (`TabSnapshot.group`): a domain, or a Google product. Empty for a new tab. */
	domain: string
	ids: string[]
	/** What the stack is called on screen. */
	name: string
	/** Stable identity for animation: the group, or a new tab's own id. */
	key: string
}

function makeStack(domain: string, ids: string[], name: string): Stack {
	return { domain, ids, name, key: domain === '' ? (ids[0] ?? '') : domain }
}

/**
 * The *arrangement* decides the order — this takes tabs already in their
 * arranged order, so a site sits where its first page falls and its pages
 * keep their order inside the dropdown. "Last opened" therefore puts the
 * site you were just on first, "Newest" the one with the newest page.
 */
export function stacks(tabs: readonly TabSnapshot[]): Stack[] {
	const groups = new Map<string, { ids: string[]; name: string; isNew: boolean }>()
	for (const tab of tabs) {
		if (tab.isPinned) continue
		const isNew = tab.group === ''
		const key = isNew ? `new:${tab.id}` : tab.group
		const group = groups.get(key)
		if (group) group.ids.push(tab.id)
		else groups.set(key, { ids: [tab.id], name: tab.groupName, isNew })
	}
	return [...groups.entries()].map(([key, group]) =>
		group.isNew ? makeStack('', group.ids, '') : makeStack(key, group.ids, group.name),
	)
}

/**
 * A–Z is the exception to "the arrangement decides": there sites themselves
 * sort by name, since "the site whose first title comes first" isn't an
 * order anyone looks for. New tabs (no site) come after them, in their own order.
 */
export function stacksByName(tabs: readonly TabSnapshot[]): Stack[] {
	const all = stacks(tabs)
	const sites = all.filter((stack) => stack.domain !== '')
	const fresh = all.filter((stack) => stack.domain === '')
	return [...sites.sort((a, b) => nameCollator.compare(a.name, b.name)), ...fresh]
}

/** The stacks as the Dropdown style shows them under an arrangement. */
export function displayedStacks(tabs: readonly TabSnapshot[], organization: TabOrganization): Stack[] {
	if (organization === 'alphabetical') return stacksByName(tabs)
	return stacks(tabs)
}

/**
 * Dropdown style, a stack dragged along the strip: the stacks in their
 * new order. Every page of the site moves with it, in its own order.
 */
export function movingStack(stackIds: readonly string[][], from: number, to: number): string[][] {
	const inRange = (index: number): boolean => index >= 0 && index < stackIds.length
	if (!inRange(from) || !inRange(to) || from === to) return [...stackIds]
	const result = [...stackIds]
	const [moved] = result.splice(from, 1)
	result.splice(to, 0, moved as string[])
	return result
}

/**
 * One id moved to `index` within a list — a page dragged inside a
 * stack's dropdown. The index is where it ends up, clamped to the list.
 */
export function moving(id: string, ids: readonly string[], index: number): string[] {
	const from = ids.indexOf(id)
	if (from < 0) return [...ids]
	const result = [...ids]
	result.splice(from, 1)
	result.splice(Math.min(Math.max(index, 0), result.length), 0, id)
	return result
}

/** `BrowserModel.pinnedCount`: the pinned run at the front of the strip. */
export function pinnedCount(tabs: readonly TabSnapshot[]): number {
	const firstLoose = tabs.findIndex((tab) => !tab.isPinned)
	return firstLoose < 0 ? tabs.length : firstLoose
}

/**
 * Port of `BrowserModel.moveTab`: moves a tab between slots, for
 * drag-to-reorder. Returns the strip's new order of ids.
 *
 * A tab can only move within its own section: dragging an unpinned tab
 * into the pinned run would silently un-pin it by position alone, which
 * isn't what grabbing a tab means. An index outside the strip changes
 * nothing — a layout and the tab list can disagree for a frame while a
 * tab closes mid-animation.
 */
export function moveTab(tabs: readonly TabSnapshot[], from: number, to: number): string[] {
	const ids = tabs.map((tab) => tab.id)
	const inRange = (index: number): boolean => index >= 0 && index < tabs.length
	if (!inRange(from) || !inRange(to) || from === to) return ids
	const pinned = pinnedCount(tabs)
	const clamped = tabs[from]?.isPinned ? Math.min(to, Math.max(pinned - 1, 0)) : Math.max(to, pinned)
	if (!inRange(clamped) || clamped === from) return ids
	const [moved] = ids.splice(from, 1)
	ids.splice(clamped, 0, moved as string)
	return ids
}

/**
 * Port of `BrowserModel.move(_:within:to:)`: a page dragged within its
 * stack's dropdown. The stack's pages swap among the places they already
 * hold, so the rest of the strip — and the stack's own place in it — stays
 * put. `index` is the page's new position among the stack's pages.
 */
export function moveWithin(tabs: readonly TabSnapshot[], id: string, stackIds: readonly string[], index: number): string[] {
	const ids = tabs.map((tab) => tab.id)
	const members = new Set(stackIds)
	const slots = ids.flatMap((tabId, slot) => (members.has(tabId) ? [slot] : []))
	const reordered = moving(id, slots.map((slot) => ids[slot] as string), index)
	slots.forEach((slot, i) => {
		ids[slot] = reordered[i] as string
	})
	return ids
}

export class TabArrangementError extends Error {
	override name = 'TabArrangementError'
}

/**
 * Lays the unpinned tabs out as these stacks, in this order; pinned tabs
 * stay in front. Refused unless it accounts for every tab exactly once.
 * `tabs` is the strip's current order; returns the new order of ids.
 */
export function arrange(tabs: readonly TabSnapshot[], stackIds: readonly string[][]): string[] {
	const byId = new Map(tabs.map((tab) => [tab.id, tab]))
	const looseIds = stackIds.flat()
	const pinnedIds = tabs.filter((tab) => tab.isPinned).map((tab) => tab.id)
	if (looseIds.some((id) => !byId.has(id))) {
		throw new TabArrangementError('arrangement names a tab that is not open')
	}
	if (new Set(looseIds).size !== looseIds.length) {
		throw new TabArrangementError('arrangement lists a tab more than once')
	}
	if (looseIds.some((id) => byId.get(id)?.isPinned)) {
		throw new TabArrangementError('arrangement includes a pinned tab')
	}
	if (pinnedIds.length + looseIds.length !== tabs.length) {
		throw new TabArrangementError('arrangement leaves out an open tab')
	}
	return [...pinnedIds, ...looseIds]
}

/**
 * Before a hand-made change in a sorted arrangement: makes what's on
 * screen the manual order, so switching to manual doesn't move anything.
 */
export function freezeDisplayedOrder(tabs: readonly TabSnapshot[], organization: TabOrganization): string[] {
	return arrange(tabs, displayedStacks(tabs, organization).map((stack) => stack.ids))
}
