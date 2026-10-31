import { mkdtempSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { describe, expect, it, vi } from 'vitest'
import { emptyLibrary, FAVOURITES_ID, favourites, folder, isFolder, link, type BookmarkFolder } from '@shared/library/library'
import {
	InvalidLibraryActionError,
	LibraryFileError,
	LibraryStore,
	loadLibraryFile,
	moveNode,
	parseLibrary,
	parseLibraryAction,
} from './library-store'

function newStore() {
	const writer = { schedule: vi.fn(), flush: vi.fn() }
	return { writer, store: new LibraryStore(emptyLibrary(), writer) }
}

describe('LibraryStore favourites and reading list', () => {
	it('adds a favourite once per address', () => {
		const { store } = newStore()
		expect(store.addFavourite('A', 'https://a.test/')).toBe('added')
		expect(store.addFavourite('A again', 'https://a.test/')).toBe('exists')
		expect(favourites(store.snapshot())).toHaveLength(1)
	})

	it('puts the newest reading list item first and ignores duplicates', () => {
		const { store } = newStore()
		store.addReading('One', 'https://1.test/')
		store.addReading('Two', 'https://2.test/')
		expect(store.addReading('Two', 'https://2.test/')).toBe('exists')
		expect(store.snapshot().readingList.map((i) => i.title)).toEqual(['Two', 'One'])
	})

	it('schedules a write on every change and none for a no-op', () => {
		const { store, writer } = newStore()
		store.addFavourite('A', 'https://a.test/')
		store.addFavourite('A', 'https://a.test/')
		expect(writer.schedule).toHaveBeenCalledTimes(1)
	})

	it('tells subscribers what changed', () => {
		const { store } = newStore()
		const listener = vi.fn()
		store.subscribe(listener)
		store.addReading('One', 'https://1.test/')
		expect(listener).toHaveBeenCalledWith(store.snapshot())
	})

	it('marks read and unread', () => {
		const { store } = newStore()
		store.addReading('One', 'https://1.test/')
		const id = store.snapshot().readingList[0]!.id
		store.apply({ type: 'setRead', id, read: true })
		expect(store.snapshot().readingList[0]!.isRead).toBe(true)
	})
})

describe('LibraryStore.apply', () => {
	it('renames, trimming and refusing blank titles', () => {
		const { store } = newStore()
		store.addFavourite('A', 'https://a.test/')
		const id = favourites(store.snapshot())[0]!.id
		store.apply({ type: 'rename', id, title: '  B  ' })
		expect(favourites(store.snapshot())[0]!.title).toBe('B')
		store.apply({ type: 'rename', id, title: '   ' })
		expect(favourites(store.snapshot())[0]!.title).toBe('B')
	})

	it('removes bookmarks and reading items but never the Favourites folder', () => {
		const { store } = newStore()
		store.addFavourite('A', 'https://a.test/')
		store.addReading('R', 'https://r.test/')
		const ids = [favourites(store.snapshot())[0]!.id, store.snapshot().readingList[0]!.id, FAVOURITES_ID]
		store.apply({ type: 'remove', ids })
		expect(favourites(store.snapshot())).toHaveLength(0)
		expect(store.snapshot().readingList).toHaveLength(0)
		expect(store.snapshot().bookmarks.map((n) => n.id)).toEqual([FAVOURITES_ID])
	})

	it('groups a selection into a new folder and returns its id', () => {
		const { store } = newStore()
		store.addFavourite('A', 'https://a.test/')
		store.addFavourite('B', 'https://b.test/')
		const [a, b] = favourites(store.snapshot())
		const id = store.apply({ type: 'newFolder', ids: [a!.id, b!.id] })
		const [grouped] = favourites(store.snapshot())
		expect(grouped!.id).toBe(id)
		expect(isFolder(grouped!) && grouped.children.map((c) => c.title)).toEqual(['A', 'B'])
	})

	it('adds an empty folder when nothing is selected', () => {
		const { store } = newStore()
		const id = store.apply({ type: 'newFolder', ids: [] })
		expect(store.snapshot().bookmarks.at(-1)).toMatchObject({ id, title: 'New Folder', children: [] })
	})

	it('reorders favourites and reading items', () => {
		const { store } = newStore()
		for (const n of ['A', 'B', 'C']) store.addFavourite(n, `https://${n}.test/`)
		const [a] = favourites(store.snapshot())
		store.apply({ type: 'move', id: a!.id, to: 3 })
		expect(favourites(store.snapshot()).map((n) => n.title)).toEqual(['B', 'C', 'A'])
		for (const n of ['X', 'Y']) store.addReading(n, `https://${n}.test/`)
		const first = store.snapshot().readingList[0]!
		store.apply({ type: 'move', id: first.id, to: 2 })
		expect(store.snapshot().readingList.map((i) => i.title)).toEqual(['X', 'Y'])
	})

	it('finds links to open, and every link in a folder', () => {
		const { store } = newStore()
		store.addFavourite('A', 'https://a.test/')
		store.addFavourite('B', 'https://b.test/')
		const [a, b] = favourites(store.snapshot())
		const folderId = store.apply({ type: 'newFolder', ids: [a!.id, b!.id] })!
		expect(store.urlsOf(folderId)).toEqual(['https://a.test/', 'https://b.test/'])
		expect(store.urlOf(a!.id)).toBe('https://a.test/')
		expect(store.urlOf(folderId)).toBeUndefined()
	})
})

describe('moveNode', () => {
	it('moves within a nested folder only', () => {
		const inner = folder('F', [link('1', 'https://1.test'), link('2', 'https://2.test')])
		const moved = moveNode([inner, link('top', 'https://t.test')], (inner.children[0] as { id: string }).id, 2)!
		expect((moved[0] as BookmarkFolder).children.map((c) => c.title)).toEqual(['2', '1'])
		expect(moved[1]!.title).toBe('top')
	})
	it('answers undefined for an unknown id', () => expect(moveNode([], 'nope', 0)).toBeUndefined())
})

describe('library file', () => {
	const scratch = () => join(mkdtempSync(join(tmpdir(), 'zing-lib-')), 'library.json')

	it('starts empty when there is no file yet', () => {
		// Both sides stamp updatedAt; a millisecond tick between them made this flaky.
		vi.useFakeTimers({ now: 1_000 })
		try {
			expect(loadLibraryFile(scratch())).toEqual(emptyLibrary())
		} finally {
			vi.useRealTimers()
		}
	})

	it('round-trips what the store saved', () => {
		const path = scratch()
		const { store, writer } = newStore()
		store.addFavourite('A', 'https://a.test/')
		writeFileSync(path, JSON.stringify(writer.schedule.mock.calls[0]![0]))
		expect(favourites(loadLibraryFile(path))[0]).toMatchObject({ title: 'A', url: 'https://a.test/' })
	})

	it('refuses a corrupt file rather than starting empty over it', () => {
		const path = scratch()
		writeFileSync(path, '{"bookmarks": [{"id": 1}], "readingList": []}')
		expect(() => loadLibraryFile(path)).toThrow(LibraryFileError)
		writeFileSync(path, 'not json')
		expect(() => loadLibraryFile(path)).toThrow(LibraryFileError)
	})

	it('restores a Favourites folder the file lacks', () => {
		const parsed = parseLibrary({ bookmarks: [], readingList: [] })
		expect(parsed.bookmarks[0]!.id).toBe(FAVOURITES_ID)
	})
})

describe('parseLibraryAction', () => {
	it('accepts each action shape', () => {
		expect(parseLibraryAction({ type: 'move', id: 'x', to: 2 })).toEqual({ type: 'move', id: 'x', to: 2 })
		expect(parseLibraryAction({ type: 'remove', ids: ['a'] })).toEqual({ type: 'remove', ids: ['a'] })
	})
	it('rejects anything else', () => {
		for (const bad of [null, { type: 'drop' }, { type: 'move', id: 'x', to: 1.5 }, { type: 'remove', ids: [1] }, { type: 'setRead', id: 'a' }]) {
			expect(() => parseLibraryAction(bad)).toThrow(InvalidLibraryActionError)
		}
	})
})
