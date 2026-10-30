// Ported from reference/krackn/Tests/library/main.swift (25 checks).
import { describe, expect, it } from 'vitest'
import {
	collect,
	emptyLibrary,
	FAVOURITES_ID,
	favourites,
	find,
	folder,
	groupIntoFolder,
	isFolder,
	link,
	move,
	readingItem,
	remove,
	rename,
	urls,
	visibleRows,
	withFavourites,
	type BookmarkFolder,
} from './library'
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

describe('bookmarks', () => {
	let lib = emptyLibrary()
	it('a fresh library has a Favourites folder', () => expect(lib.bookmarks[0]?.id).toBe(FAVOURITES_ID))
	lib = withFavourites(lib, [link('Apple', 'https://apple.com'), link('Wiki', 'https://wikipedia.org')])
	it('favourites live inside that folder', () => expect((lib.bookmarks[0] as BookmarkFolder).children).toHaveLength(2))
	const docs = folder('Docs', [link('MDN', 'https://developer.mozilla.org'), folder('Inner', [link('Swift', 'https://swift.org')])])
	lib = { ...lib, bookmarks: [...lib.bookmarks, docs] }

	it("the Favourites folder can't be deleted", () => {
		expect(remove(new Set([FAVOURITES_ID]), lib.bookmarks)[0]?.id).toBe(FAVOURITES_ID)
	})

	const mdn = docs.children[0]!
	const noMdn = remove(new Set([mdn.id]), lib.bookmarks)
	it('removes at any depth', () => expect(find(mdn.id, noMdn)).toBeUndefined())
	it('leaves siblings alone', () => expect(find(docs.children[1]!.id, noMdn)).toBeDefined())

	it('rename trims', () => expect(find(mdn.id, rename(mdn.id, '  Mozilla  ', lib.bookmarks))?.title).toBe('Mozilla'))
	it('blank rename is ignored', () => expect(find(mdn.id, rename(mdn.id, '  ', lib.bookmarks))?.title).toBe('MDN'))

	it('Ctrl-click a folder opens every link inside, nested too', () => {
		expect(urls(docs)).toEqual(['https://developer.mozilla.org', 'https://swift.org'])
	})

	const fav = favourites(lib)
	const grouped = groupIntoFolder(new Set([fav[0]!.id, fav[1]!.id]), 'New Folder', lib.bookmarks)
	const newFolder = grouped.folderId ? find(grouped.folderId, grouped.nodes) : undefined
	it('New Folder wraps the selection', () => {
		expect(newFolder && isFolder(newFolder) ? newFolder.children.length : 0).toBe(2)
	})
	it('the folder lands where the first item was', () => {
		expect((grouped.nodes[0] as BookmarkFolder).children[0]?.id).toBe(grouped.folderId)
	})
	it("the items aren't duplicated", () => expect(collect(new Set([fav[0]!.id]), grouped.nodes)).toHaveLength(1))

	describe("keyboard navigation follows what's visible", () => {
		it('collapsed: only top-level rows', () => expect(visibleRows(lib.bookmarks, new Set())).toHaveLength(lib.bookmarks.length))
		const open = visibleRows(lib.bookmarks, new Set([FAVOURITES_ID, docs.id]))
		it('open folders add their children in order', () => {
			expect(open.map((row) => row.node.title)).toEqual(['Favourites', 'Apple', 'Wiki', 'Docs', 'MDN', 'Inner'])
		})
		it('children are indented one level', () => {
			expect(open[1]?.depth).toBe(1)
			expect(open[0]?.depth).toBe(0)
		})
	})

	it('the library round-trips through JSON', () => {
		const withReading = { ...lib, readingList: [readingItem('Article', 'https://example.com/a')] }
		expect(JSON.parse(JSON.stringify(withReading))).toEqual(withReading)
	})
})

describe('reordering', () => {
	it('move right', () => expect(move(['a', 'b', 'c'], 0, 2)).toEqual(['b', 'a', 'c']))
	it('move to the end', () => expect(move(['a', 'b', 'c'], 0, 3)).toEqual(['b', 'c', 'a']))
	it('out of range is a no-op', () => expect(move(['a', 'b'], 5, 0)).toEqual(['a', 'b']))
})

describe('sync-ready records (new in Zing)', () => {
	it('every node carries a UUID and updatedAt', () => {
		const node = link('A', 'https://a.example', 42)
		expect(node.id).toMatch(/^[0-9a-f-]{36}$/)
		expect(node.updatedAt).toBe(42)
	})
	it('rename bumps updatedAt on the renamed node only', () => {
		const a = link('A', 'https://a.example', 1)
		const b = link('B', 'https://b.example', 1)
		const out = rename(a.id, 'A2', [a, b], 99)
		expect(out[0]?.updatedAt).toBe(99)
		expect(out[1]?.updatedAt).toBe(1)
	})
})
