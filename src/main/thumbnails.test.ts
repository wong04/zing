import { describe, expect, it } from 'vitest'
import type { TabList, TabView } from '@shared/ipc'
import { overviewState, ThumbnailCache } from './thumbnails'

describe('ThumbnailCache', () => {
	it('drops the least recently used picture at capacity', () => {
		const cache = new ThumbnailCache(2)
		cache.set('a', 'A')
		cache.set('b', 'B')
		cache.get('a')
		cache.set('c', 'C')
		expect(cache.size).toBe(2)
		expect(cache.get('b')).toBeUndefined()
		expect(cache.get('a')).toBe('A')
	})

	it('replaces a tab\'s picture instead of growing', () => {
		const cache = new ThumbnailCache(2)
		cache.set('a', 'old')
		cache.set('a', 'new')
		expect(cache.size).toBe(1)
		expect(cache.get('a')).toBe('new')
	})

	it('forgets a closed tab', () => {
		const cache = new ThumbnailCache()
		cache.set('a', 'A')
		cache.delete('a')
		expect(cache.get('a')).toBeUndefined()
	})
})

describe('overviewState', () => {
	const tab = (id: string): TabView => ({
		id,
		url: `https://${id}.test/`,
		title: id.toUpperCase(),
		pinned: false,
		createdAt: 0,
		lastActiveAt: 0,
		updatedAt: 0,
		loading: false,
		canGoBack: false,
		canGoForward: false,
	})

	it('lists every tab in order and marks the active one', () => {
		const cache = new ThumbnailCache()
		cache.set('b', 'pic')
		const list = { tabs: [tab('a'), tab('b')], activeId: 'b' } as TabList
		const state = overviewState(list, cache)
		expect(state.tabs.map((t) => t.id)).toEqual(['a', 'b'])
		expect(state.tabs.map((t) => t.active)).toEqual([false, true])
		expect(state.tabs.map((t) => t.thumbnail)).toEqual([undefined, 'pic'])
	})
})
