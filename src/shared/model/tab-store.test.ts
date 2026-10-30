import { describe, expect, it } from 'vitest'
import { NEW_TAB_URL } from './tab'
import { TabStore, UnknownTabError } from './tab-store'
import { zoomInFactor, zoomOutFactor } from './zoom'

function clockFrom(start: number): () => number {
	let t = start
	return () => t++
}

function storeWith(...urls: string[]): TabStore {
	const store = new TabStore(clockFrom(1000))
	for (const url of urls) store.add(url)
	return store
}

const urls = (store: TabStore): string[] => store.tabs.map((t) => t.url)

describe('TabStore', () => {
	it('creates records with stable UUIDs and timestamps, without activating', () => {
		const store = storeWith('https://a.test')
		const [tab] = store.tabs
		expect(tab?.id).toMatch(/^[0-9a-f-]{36}$/)
		expect(tab).toMatchObject({ pinned: false, createdAt: 1000, updatedAt: 1000, lastActiveAt: 1000 })
		expect(store.activeId).toBeUndefined()
	})

	it('activating stamps lastActiveAt but not updatedAt', () => {
		const store = storeWith('https://a.test')
		const id = store.tabs[0]!.id
		const active = store.activate(id)
		expect(store.activeId).toBe(id)
		expect(active.lastActiveAt).toBeGreaterThan(active.updatedAt)
	})

	it('update bumps updatedAt only when something changed', () => {
		const store = storeWith('https://a.test')
		const id = store.tabs[0]!.id
		const before = store.get(id)
		expect(store.update(id, { url: 'https://a.test' })).toBe(before)
		const after = store.update(id, { title: 'A' })
		expect(after.title).toBe('A')
		expect(after.updatedAt).toBeGreaterThan(before.updatedAt)
	})

	it('closing the active tab activates the tab that slides into its slot', () => {
		const store = storeWith('https://a.test', 'https://b.test', 'https://c.test')
		const [, b, c] = store.tabs
		store.activate(b!.id)
		expect(store.close(b!.id).nextActiveId).toBe(c!.id)
		expect(store.activeId).toBe(c!.id)
	})

	it('closing the last active tab activates the new last tab', () => {
		const store = storeWith('https://a.test', 'https://b.test')
		const [a, b] = store.tabs
		store.activate(b!.id)
		store.close(b!.id)
		expect(store.activeId).toBe(a!.id)
	})

	it('closing every tab leaves the store empty with nothing active', () => {
		const store = storeWith('https://a.test')
		const id = store.tabs[0]!.id
		store.activate(id)
		expect(store.close(id).nextActiveId).toBeUndefined()
		expect(store.tabs).toHaveLength(0)
		expect(store.activeId).toBeUndefined()
	})

	it('reopens closed web pages newest first, but not blank tabs', () => {
		const store = storeWith('https://a.test', NEW_TAB_URL, 'https://c.test')
		for (const t of [...store.tabs]) store.close(t.id)
		expect(store.reopenClosed()?.url).toBe('https://c.test')
		expect(store.reopenClosed()?.url).toBe('https://a.test')
		expect(store.reopenClosed()).toBeUndefined()
		expect(store.canReopen).toBe(false)
	})

	it('a reopened tab gets a fresh id', () => {
		const store = storeWith('https://a.test')
		const old = store.tabs[0]!
		store.close(old.id)
		expect(store.reopenClosed()?.id).not.toBe(old.id)
	})

	it('pinning moves a tab to the end of the pinned run; unpinning to the start of the rest', () => {
		const store = storeWith('https://a.test', 'https://b.test', 'https://c.test')
		const [a, b, c] = store.tabs
		store.pin(c!.id)
		store.pin(b!.id)
		expect(urls(store)).toEqual(['https://c.test', 'https://b.test', 'https://a.test'])
		store.unpin(c!.id)
		expect(urls(store)).toEqual(['https://b.test', 'https://c.test', 'https://a.test'])
		expect(store.get(a!.id).pinned).toBe(false)
	})

	it('moves stay within their section', () => {
		const store = storeWith('https://a.test', 'https://b.test', 'https://c.test')
		const [a, , c] = store.tabs
		store.pin(a!.id)
		store.move(c!.id, 0)
		expect(urls(store)).toEqual(['https://a.test', 'https://c.test', 'https://b.test'])
		store.move(a!.id, 2)
		expect(urls(store)[0]).toBe('https://a.test')
	})

	it('a popup lands right after its opener', () => {
		const store = storeWith('https://a.test', 'https://b.test')
		const opener = store.tabs[0]!
		store.addAfter(opener.id, 'https://popup.test')
		expect(urls(store)).toEqual(['https://a.test', 'https://popup.test', 'https://b.test'])
	})

	it('next, previous and Ctrl+N resolve through the active tab', () => {
		const store = storeWith('https://a.test', 'https://b.test', 'https://c.test')
		const [a, b, c] = store.tabs
		store.activate(c!.id)
		expect(store.nextId()).toBe(a!.id)
		expect(store.previousId()).toBe(b!.id)
		expect(store.idForShortcut(9)).toBe(c!.id)
		expect(store.idForShortcut(4)).toBeUndefined()
	})

	it('raises UnknownTabError for an id it does not hold', () => {
		const store = storeWith()
		expect(() => store.activate('nope')).toThrow(UnknownTabError)
		expect(() => store.close('nope')).toThrow(UnknownTabError)
	})
})

describe('zoom steps', () => {
	it('steps through Chromium factors and stops at the ends', () => {
		expect(zoomInFactor(1)).toBe(1.1)
		expect(zoomOutFactor(1)).toBe(0.9)
		expect(zoomInFactor(5)).toBe(5)
		expect(zoomOutFactor(0.25)).toBe(0.25)
	})

	it('snaps an off-step factor to the next step', () => {
		expect(zoomInFactor(1.05)).toBe(1.1)
		expect(zoomOutFactor(1.05)).toBe(1)
	})
})
