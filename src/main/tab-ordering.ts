import {
	arrange,
	displayedStacks,
	dragSwitchesToManual,
	freezeDisplayedOrder,
	moveTab,
	moveWithin,
	movingStack,
	order,
	type Stack,
	type TabOrganization,
	type TabSnapshot,
} from '@shared/browser/tabOrganizer'
import type { TabId, TabRecord } from '@shared/model'
import type { SettingsStore } from '@shared/settings'
import { snapshotOf } from '@shared/tabs/slots'

/** The slice of TabService the arrangement logic needs, so it runs without Electron. */
export interface OrderedTabs {
	readonly records: readonly TabRecord[]
	readonly activeId: TabId | undefined
	applyOrder(ids: readonly TabId[]): void
}

export interface OrderingHooks {
	readonly notify: (text: string) => void
}

export class UnknownStackError extends Error {
	constructor(readonly key: string) {
		super(`No stack with key ${key}`)
		this.name = 'UnknownStackError'
	}
}

const SETTLE_MS = 250
const MANUAL_NOTICE = 'Tabs are now in manual order. Change it from the tab strip menu.'

/**
 * Keeps the tab list in the order the `tabOrganization` setting asks for and
 * turns drags into list changes. Pinned tabs are never sorted (the organiser
 * leaves them alone), and a hand-made change under a sorted arrangement first
 * freezes what is on screen as the manual order, so nothing jumps.
 */
export class TabOrdering {
	#timer: ReturnType<typeof setTimeout> | undefined

	constructor(
		private readonly tabs: OrderedTabs,
		private readonly settings: SettingsStore,
		private readonly hooks: OrderingHooks,
	) {}

	get organization(): TabOrganization {
		return this.settings.get('tabOrganization')
	}

	get dropdowns(): boolean {
		return this.settings.get('tabDropdowns')
	}

	/** The stacks as the Dropdown style shows them now, pinned tabs excluded. */
	stacks(): Stack[] {
		return displayedStacks(this.#snapshots(), this.organization)
	}

	/** Re-sorts now. Manual does nothing, which is why dragging works there. */
	reorganize(): void {
		if (this.organization === 'manual') return
		this.tabs.applyOrder(order(this.#snapshots(), this.organization))
	}

	/**
	 * A new tab has no site until its page loads, so sorting on creation alone
	 * left "By site" looking broken. The debounce folds a redirect chain into
	 * one move instead of a tab hopping across the strip.
	 */
	scheduleReorganize(): void {
		if (this.organization === 'manual') return
		clearTimeout(this.#timer)
		this.#timer = setTimeout(() => this.reorganize(), SETTLE_MS)
	}

	cancel(): void {
		clearTimeout(this.#timer)
	}

	setOrganization(organization: TabOrganization): void {
		this.settings.set('tabOrganization', organization)
		this.reorganize()
	}

	setDropdowns(on: boolean): void {
		this.settings.set('tabDropdowns', on)
	}

	/** A tab chip dragged to a new position in the strip (Separate style, or a pinned tab). */
	move(id: TabId, to: number): void {
		this.#leaveSortedOrder()
		const snaps = this.#snapshots()
		const from = snaps.findIndex((s) => s.id === id)
		this.tabs.applyOrder(moveTab(snaps, from, to))
		this.reorganize()
	}

	/** Dropdown style: a stack dragged along the strip with all its pages. `to` is its place among the stacks. */
	moveStack(key: string, to: number): void {
		this.#freezeAndLeaveSortedOrder()
		const stacks = this.stacks()
		const from = stacks.findIndex((s) => s.key === key)
		if (from < 0) throw new UnknownStackError(key)
		const next = movingStack(
			stacks.map((s) => s.ids),
			from,
			to,
		)
		this.tabs.applyOrder(arrange(this.#snapshots(), next))
		this.reorganize()
	}

	/** A page dragged inside its stack's dropdown. `index` is its new place among the stack's pages. */
	moveWithin(id: TabId, key: string, index: number): void {
		this.#freezeAndLeaveSortedOrder()
		const stack = this.stacks().find((s) => s.key === key)
		if (!stack) throw new UnknownStackError(key)
		this.tabs.applyOrder(moveWithin(this.#snapshots(), id, stack.ids, index))
		this.reorganize()
	}

	#leaveSortedOrder(): void {
		if (!dragSwitchesToManual[this.organization]) return
		this.settings.set('tabOrganization', 'manual')
		this.hooks.notify(MANUAL_NOTICE)
	}

	#freezeAndLeaveSortedOrder(): void {
		if (!dragSwitchesToManual[this.organization]) return
		this.tabs.applyOrder(freezeDisplayedOrder(this.#snapshots(), this.organization))
		this.#leaveSortedOrder()
	}

	#snapshots(): TabSnapshot[] {
		return this.tabs.records.map((r) => snapshotOf(r, r.id === this.tabs.activeId))
	}
}
