import type { OverviewState, TabList } from '@shared/ipc'
import type { TabId } from '@shared/model'

/**
 * Tab pictures, kept in memory only and capped. Least recently used goes
 * first, so a long session that opens the overview now and then never holds
 * a picture of every page it ever had open.
 */
export class ThumbnailCache {
	readonly #entries = new Map<TabId, string>()

	constructor(private readonly capacity = 24) {}

	get size(): number {
		return this.#entries.size
	}

	get(id: TabId): string | undefined {
		const value = this.#entries.get(id)
		if (value === undefined) return undefined
		this.#entries.delete(id)
		this.#entries.set(id, value)
		return value
	}

	set(id: TabId, thumbnail: string): void {
		this.#entries.delete(id)
		this.#entries.set(id, thumbnail)
		while (this.#entries.size > this.capacity) {
			const oldest = this.#entries.keys().next()
			if (oldest.done) return
			this.#entries.delete(oldest.value)
		}
	}

	delete(id: TabId): void {
		this.#entries.delete(id)
	}
}

export function overviewState(list: TabList, thumbnails: ThumbnailCache): OverviewState {
	return {
		tabs: list.tabs.map((tab) => ({
			id: tab.id,
			title: tab.title,
			url: tab.url,
			icon: tab.icon,
			thumbnail: thumbnails.get(tab.id),
			active: tab.id === list.activeId,
		})),
	}
}
