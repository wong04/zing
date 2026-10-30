import { describe, expect, it } from 'vitest'
import { historyMenuItems, type HistoryEntry } from './history-menu'

const entries: HistoryEntry[] = ['a', 'b', 'c', 'd', 'e'].map((t) => ({ url: `https://x.test/${t}`, title: t }))

describe('historyMenuItems', () => {
	it('lists back entries nearest first', () => {
		expect(historyMenuItems(entries, 3, 'back').map((i) => i.label)).toEqual(['c', 'b', 'a'])
	})

	it('lists forward entries nearest first', () => {
		expect(historyMenuItems(entries, 1, 'forward').map((i) => i.index)).toEqual([2, 3, 4])
	})

	it('is empty at the ends', () => {
		expect(historyMenuItems(entries, 0, 'back')).toEqual([])
		expect(historyMenuItems(entries, 4, 'forward')).toEqual([])
	})

	it('falls back to the URL and caps the list', () => {
		const many = Array.from({ length: 40 }, (_, i) => ({ url: `https://x.test/${i}`, title: '' }))
		const items = historyMenuItems(many, 39, 'back', 5)
		expect(items).toHaveLength(5)
		expect(items[0]?.label).toBe('https://x.test/38')
	})
})
