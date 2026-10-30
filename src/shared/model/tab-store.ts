import { ClosedStack, nextTab, previousTab, tabForShortcut } from '../library/tabNavigation'
import { createTabRecord, isRestorable, type TabContentPatch, type TabId, type TabRecord } from './tab'

export class UnknownTabError extends Error {
	constructor(readonly tabId: TabId) {
		super(`No tab with id ${tabId}`)
		this.name = 'UnknownTabError'
	}
}

export interface ClosedTab {
	readonly url: string
	readonly title: string
	readonly pinned: boolean
}

export interface CloseResult {
	readonly closed: TabRecord
	/** Set when the closed tab was active and another tab took its place. */
	readonly nextActiveId?: TabId
}

/**
 * The ordered tab list of one window, as pure data: no renderer, no Electron.
 * Pinned tabs always form a run at the front, as in Krackn's BrowserModel.
 */
export class TabStore {
	#tabs: TabRecord[] = []
	#activeId: TabId | undefined
	readonly #closed = new ClosedStack<ClosedTab>()

	constructor(private readonly now: () => number = Date.now) {}

	get tabs(): readonly TabRecord[] {
		return this.#tabs
	}

	get activeId(): TabId | undefined {
		return this.#activeId
	}

	get active(): TabRecord | undefined {
		return this.#activeId === undefined ? undefined : this.get(this.#activeId)
	}

	get canReopen(): boolean {
		return !this.#closed.isEmpty
	}

	get pinnedCount(): number {
		return this.#tabs.filter((t) => t.pinned).length
	}

	has(id: TabId): boolean {
		return this.#tabs.some((t) => t.id === id)
	}

	get(id: TabId): TabRecord {
		return this.#tabs[this.#indexOf(id)]!
	}

	indexOf(id: TabId): number {
		return this.#indexOf(id)
	}

	/** Appends a new tab without activating it. */
	add(url: string): TabRecord {
		const record = createTabRecord(url, this.now())
		this.#tabs.push(record)
		return record
	}

	/** Inserts a new tab right after another, the way a popup lands beside its opener. */
	addAfter(anchor: TabId, url: string): TabRecord {
		const record = createTabRecord(url, this.now())
		const at = Math.max(this.#indexOf(anchor) + 1, this.pinnedCount)
		this.#tabs.splice(at, 0, record)
		return record
	}

	/** A tab brought back from a saved session: a record only, placed by its pinned state. */
	addRestored(content: Pick<TabRecord, 'url' | 'title' | 'pinned'>): TabRecord {
		const record = { ...createTabRecord(content.url, this.now()), title: content.title, pinned: content.pinned }
		this.#tabs.splice(record.pinned ? this.pinnedCount : this.#tabs.length, 0, record)
		return record
	}

	/** Marks a tab as just used, e.g. the moment it stops being the active one. */
	touch(id: TabId): void {
		const index = this.#indexOf(id)
		this.#tabs[index] = { ...this.#tabs[index]!, lastActiveAt: this.now() }
	}

	activate(id: TabId): TabRecord {
		const index = this.#indexOf(id)
		const now = this.now()
		const record = { ...this.#tabs[index]!, lastActiveAt: now }
		this.#tabs[index] = record
		this.#activeId = id
		return record
	}

	update(id: TabId, patch: TabContentPatch): TabRecord {
		const index = this.#indexOf(id)
		const current = this.#tabs[index]!
		const changed = (Object.keys(patch) as (keyof TabContentPatch)[]).some((k) => patch[k] !== current[k])
		if (!changed) return current
		const record = { ...current, ...patch, updatedAt: this.now() }
		this.#tabs[index] = record
		return record
	}

	/**
	 * Removes a tab. If it was active, the tab that slides into its slot (or the
	 * new last tab) becomes active, matching Krackn. Leaves an empty list empty;
	 * the caller decides what an empty window means.
	 */
	close(id: TabId): CloseResult {
		const index = this.#indexOf(id)
		const [closed] = this.#tabs.splice(index, 1) as [TabRecord]
		if (isRestorable(closed)) this.#closed.push({ url: closed.url, title: closed.title, pinned: closed.pinned })
		if (this.#activeId !== id) return { closed }
		this.#activeId = undefined
		const next = this.#tabs[Math.min(index, this.#tabs.length - 1)]
		if (!next) return { closed }
		this.activate(next.id)
		return { closed, nextActiveId: next.id }
	}

	/** Brings back the most recently closed tab, unactivated. Undefined when there is none. */
	reopenClosed(): TabRecord | undefined {
		const closed = this.#closed.pop()
		if (!closed) return undefined
		const record = { ...createTabRecord(closed.url, this.now()), title: closed.title, pinned: closed.pinned }
		const at = record.pinned ? this.pinnedCount : this.#tabs.length
		this.#tabs.splice(at, 0, record)
		return record
	}

	/**
	 * Moves a tab within its own section. Dragging a loose tab into the pinned
	 * run would silently pin it by position alone, so the target is clamped.
	 */
	move(id: TabId, toIndex: number): void {
		const from = this.#indexOf(id)
		const pinned = this.pinnedCount
		const record = this.#tabs[from]!
		const clamped = record.pinned
			? Math.min(Math.max(toIndex, 0), pinned - 1)
			: Math.min(Math.max(toIndex, pinned), this.#tabs.length - 1)
		if (clamped === from) return
		this.#tabs.splice(from, 1)
		this.#tabs.splice(clamped, 0, record)
	}

	/** Pinned tabs live at the front; a newly pinned tab joins the end of that run. */
	pin(id: TabId): void {
		this.#setPinned(id, true)
	}

	/** Drops to the start of the loose section: its old position no longer means anything. */
	unpin(id: TabId): void {
		this.#setPinned(id, false)
	}

	nextId(): TabId | undefined {
		return this.#idAt(nextTab(this.#activeIndex(), this.#tabs.length))
	}

	previousId(): TabId | undefined {
		return this.#idAt(previousTab(this.#activeIndex(), this.#tabs.length))
	}

	idForShortcut(n: number): TabId | undefined {
		return this.#idAt(tabForShortcut(n, this.#tabs.length))
	}

	#setPinned(id: TabId, pinned: boolean): void {
		const index = this.#indexOf(id)
		const current = this.#tabs[index]!
		if (current.pinned === pinned) return
		this.#tabs.splice(index, 1)
		const record = { ...current, pinned, updatedAt: this.now() }
		this.#tabs.splice(this.pinnedCount, 0, record)
	}

	#activeIndex(): number {
		return this.#activeId === undefined ? -1 : this.#indexOf(this.#activeId)
	}

	#idAt(index: number | undefined): TabId | undefined {
		return index === undefined ? undefined : this.#tabs[index]?.id
	}

	#indexOf(id: TabId): number {
		const index = this.#tabs.findIndex((t) => t.id === id)
		if (index === -1) throw new UnknownTabError(id)
		return index
	}
}
