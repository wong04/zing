import { describe, expect, it } from 'vitest'
import { FAVOURITES_ID, folder, PINNED_ID, type BookmarkNode, type BrowserLibrary } from '@shared/library/library'
import { dropAction, removalPrompt } from './panelEdits'

const mark = (id: string): BookmarkNode => ({ id, title: id, url: `https://${id}.test/`, updatedAt: 0 })
const reading = (id: string) => ({ id, title: id, url: `https://${id}.test/`, added: 0, isRead: false, updatedAt: 0 })

const library: BrowserLibrary = {
	bookmarks: [
		folder('Favourites', [mark('a'), mark('b'), mark('c')], FAVOURITES_ID, 0),
		folder('Pinned', [], PINNED_ID, 0),
		folder('Work', [mark('x'), folder('Inner', [mark('y')], 'inner', 0)], 'work', 0),
		folder('Empty', [], 'empty', 0),
	],
	readingList: [reading('r1'), reading('r2'), reading('r3')],
}

describe('dropping a row on a row in the Bookmarks panel', () => {
	it('moving down takes the place of the row dropped on', () => {
		expect(dropAction(library, 'bookmarks', 'a', 'c')).toEqual({ type: 'move', id: 'a', to: 3 })
	})
	it('moving up takes the place of the row dropped on', () => {
		expect(dropAction(library, 'bookmarks', 'c', 'a')).toEqual({ type: 'move', id: 'c', to: 0 })
	})
	it('a row from another folder lands in the dropped-on row’s folder, at its place', () => {
		expect(dropAction(library, 'bookmarks', 'x', 'b')).toEqual({ type: 'moveInto', id: 'x', parent: FAVOURITES_ID, to: 1 })
		expect(dropAction(library, 'bookmarks', 'a', 'y')).toEqual({ type: 'moveInto', id: 'a', parent: 'inner', to: 0 })
	})
	it('a row dropped on itself or inside itself goes nowhere', () => {
		expect(dropAction(library, 'bookmarks', 'a', 'a')).toBeUndefined()
		expect(dropAction(library, 'bookmarks', 'work', 'y')).toBeUndefined()
	})
	it('a nested row dropped on a top-level row goes nowhere: the top level is no folder to move into', () => {
		expect(dropAction(library, 'bookmarks', 'a', 'work')).toBeUndefined()
	})
	it('the Reading List reorders the same way', () => {
		expect(dropAction(library, 'readingList', 'r1', 'r3')).toEqual({ type: 'move', id: 'r1', to: 3 })
		expect(dropAction(library, 'readingList', 'r3', 'r1')).toEqual({ type: 'move', id: 'r3', to: 0 })
	})
})

describe('what deleting asks first', () => {
	it('a single bookmark or an empty folder goes at once', () => {
		expect(removalPrompt(library, 'bookmarks', ['a'])).toBeUndefined()
		expect(removalPrompt(library, 'bookmarks', ['empty'])).toBeUndefined()
		expect(removalPrompt(library, 'readingList', ['r1'])).toBeUndefined()
	})
	it('a folder with things in it asks, saying how many bookmarks go', () => {
		expect(removalPrompt(library, 'bookmarks', ['work'])).toBe('Delete 2 bookmarks and 2 folders?')
	})
	it('a selection of several asks', () => {
		expect(removalPrompt(library, 'bookmarks', ['a', 'b'])).toBe('Delete 2 bookmarks?')
		expect(removalPrompt(library, 'readingList', ['r1', 'r2'])).toBe('Delete 2 Reading List items?')
	})
	it('a fixed folder in the selection is not counted: it stays', () => {
		expect(removalPrompt(library, 'bookmarks', [FAVOURITES_ID, 'x'])).toBeUndefined()
	})
})
