import { describe, expect, it } from 'vitest'
import { TabStore } from '../model/tab-store'

describe('TabStore session support', () => {
	it('places restored pinned tabs in the pinned run and keeps title and url', () => {
		const store = new TabStore(() => 10)
		store.addRestored({ url: 'https://a.test/', title: 'A', pinned: false })
		store.addRestored({ url: 'https://b.test/', title: 'B', pinned: true })
		expect(store.tabs.map((t) => t.title)).toEqual(['B', 'A'])
		expect(store.tabs[0]?.url).toBe('https://b.test/')
		expect(store.activeId).toBeUndefined()
	})

	it('touch refreshes lastActiveAt without activating', () => {
		let now = 1
		const store = new TabStore(() => now)
		const a = store.add('https://a.test/')
		now = 99
		store.touch(a.id)
		expect(store.get(a.id).lastActiveAt).toBe(99)
		expect(store.activeId).toBeUndefined()
	})
})
