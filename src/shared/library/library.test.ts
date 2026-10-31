// Ported from reference/krackn/Tests/library/main.swift (25 checks).
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import {
	collect,
	emptyLibrary,
	FAVOURITES_ID,
	favourites,
	find,
	groupIntoFolder,
	link,
	move,
	readingItem,
	remove,
	rename,
	urls,
	visibleRows,
	withFavourites,
	type BookmarkNode,
	type BrowserLibrary,
	type ReadingItem,
} from './library'
import vectors from './library.vectors.json'
import { ClosedStack, nextTab, previousTab, tabForShortcut } from './tabNavigation'

describe('tab shortcuts', () => {
	it('Ctrl+Tab wraps last to first', () => expect(nextTab(4, 5)).toBe(0))
	it('Ctrl+Tab moves forward', () => expect(nextTab(1, 5)).toBe(2))
	it('Ctrl+Shift+Tab wraps first to last', () => expect(previousTab(0, 5)).toBe(4))
	it('Ctrl+1 is the first tab', () => expect(tabForShortcut(1, 5)).toBe(0))
	it('Ctrl+5 is the fifth', () => expect(tabForShortcut(5, 5)).toBe(4))
	it('Ctrl+7 with five tabs does nothing', () => expect(tabForShortcut(7, 5)).toBeUndefined())
	it('Ctrl+9 is always the last', () => expect(tabForShortcut(9, 12)).toBe(11))
	it('Ctrl+9 with one tab is that tab', () => expect(tabForShortcut(9, 1)).toBe(0))
	it('no tabs, no target', () => expect(nextTab(0, 0)).toBeUndefined())
})

describe('reopen closed tabs: newest first, bounded', () => {
	const closed = new ClosedStack<string>(3)
	for (const t of ['a', 'b', 'c', 'd']) closed.push(t)
	it('capped at capacity', () => expect(closed.items).toEqual(['b', 'c', 'd']))
	it('pops newest first', () => {
		expect(closed.pop()).toBe('d')
		expect(closed.pop()).toBe('c')
	})
})

interface Case {
	name: string
	[key: string]: unknown
}

const tree = vectors.tree.bookmarks as BookmarkNode[]
const nodesOf = (c: Case) => (c['nodes'] ?? tree) as BookmarkNode[]
const set = (value: unknown) => new Set(value as string[])

// The functions that mint ids or stamp times get a fixed id and clock, so results compare exactly.
let nextId = 0
beforeEach(() => {
	nextId = 0
	vi.spyOn(crypto, 'randomUUID').mockImplementation(() => `id-${++nextId}` as ReturnType<typeof crypto.randomUUID>)
	vi.useFakeTimers({ now: 1_000 })
})
afterEach(() => {
	vi.useRealTimers()
	vi.restoreAllMocks()
})

describe('library vectors', () => {
	it('a fresh library has a Favourites folder', () => {
		vi.setSystemTime(vectors.emptyLibrary.now)
		expect(emptyLibrary()).toEqual(vectors.emptyLibrary.expected)
		expect(emptyLibrary().bookmarks[0]?.id).toBe(FAVOURITES_ID)
	})
	it.each(vectors.withFavourites)('withFavourites: $name', (c) => {
		vi.setSystemTime(c.now)
		expect(withFavourites(c.library as BrowserLibrary, c.children as BookmarkNode[])).toEqual(c.expected)
	})
	it.each(vectors.favourites)('favourites: $name', (c) => {
		expect(favourites(c.library as BrowserLibrary).map((n) => n.id)).toEqual(c.expected)
	})
	it.each(vectors.remove)('remove: $name', (c) => expect(remove(set(c.ids), tree)).toEqual(c.expected))
	it.each(vectors.rename)('rename: $name', (c) => {
		expect(rename(c.id, c.title, nodesOf(c as Case), c.now)).toEqual(c.expected)
	})
	it.each(vectors.find)('find: $name', (c) => expect(find(c.id, tree)?.title ?? null).toBe(c.expected))
	it.each(vectors.collect)('collect: $name', (c) => expect(collect(set(c.ids), tree).map((n) => n.id)).toEqual(c.expected))
	it.each(vectors.urls)('urls: $name', (c) => {
		const nodes = nodesOf(c as Case)
		expect(urls(find(c.id, nodes)!)).toEqual(c.expected)
	})
	it.each(vectors.groupIntoFolder)('groupIntoFolder: $name', (c) => {
		vi.setSystemTime(c.now)
		vi.spyOn(crypto, 'randomUUID').mockImplementation(() => c.newId as ReturnType<typeof crypto.randomUUID>)
		const grouped = groupIntoFolder(set(c.ids), c.title, tree)
		expect({ folderId: grouped.folderId ?? null, nodes: grouped.nodes }).toEqual(c.expected)
	})
	it.each(vectors.visibleRows)('visibleRows: $name', (c) => {
		const rows = visibleRows(tree, set(c.expanded)).map((r) => ({ title: r.node.title, depth: r.depth }))
		expect(rows).toEqual(c.expected)
	})
	it.each(vectors.move)('move: $name', (c) => {
		const before = [...c.list]
		expect(move(c.list, c.from, c.to)).toEqual(c.expected)
		expect(c.list).toEqual(before)
	})
	it('the library round-trips through JSON', () => {
		const library = vectors.serialised.library
		expect(JSON.parse(JSON.stringify(library))).toEqual(library)
	})
})

describe('sync-ready records (new in Zing)', () => {
	it('every node carries a UUID and updatedAt', () => {
		vi.restoreAllMocks()
		const node = link('A', 'https://a.example', 42)
		expect(node.id).toMatch(/^[0-9a-f-]{36}$/)
		expect(node.updatedAt).toBe(42)
	})
	it('a reading item starts unread and stamps added', () => {
		const item: ReadingItem = readingItem('Article', 'https://example.com/a', 7)
		expect(item).toMatchObject({ added: 7, isRead: false, updatedAt: 7 })
	})
})
