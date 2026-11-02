import { describe, expect, it } from 'vitest'
import { edgeDropAt, type DropPane } from './splitDrop'

const page: DropPane = { tabId: 'page', rect: { x: 240, y: 40, width: 1000, height: 800 }, panes: 1 }

describe('edgeDropAt', () => {
	it('picks the edge the pointer is near, and the half of the page on that side', () => {
		expect(edgeDropAt(260, 400, [page], 'dragged')).toEqual({ tabId: 'page', edge: 'left', zone: { x: 240, y: 40, width: 500, height: 800 } })
		expect(edgeDropAt(1230, 400, [page], 'dragged')).toEqual({ tabId: 'page', edge: 'right', zone: { x: 740, y: 40, width: 500, height: 800 } })
		expect(edgeDropAt(700, 60, [page], 'dragged')?.edge).toBe('top')
		expect(edgeDropAt(700, 830, [page], 'dragged')).toEqual({ tabId: 'page', edge: 'bottom', zone: { x: 240, y: 440, width: 1000, height: 400 } })
	})

	it('aims at nothing in the middle, outside the pages, at a full split, or at the tab’s own page', () => {
		expect(edgeDropAt(740, 440, [page], 'dragged')).toBeUndefined()
		expect(edgeDropAt(100, 400, [page], 'dragged')).toBeUndefined()
		expect(edgeDropAt(260, 400, [{ ...page, panes: 4 }], 'dragged')).toBeUndefined()
		expect(edgeDropAt(260, 400, [page], 'page')).toBeUndefined()
	})

	it('finds the pane under the pointer in a split', () => {
		const left: DropPane = { tabId: 'a', rect: { x: 240, y: 40, width: 498, height: 800 }, panes: 2 }
		const right: DropPane = { tabId: 'b', rect: { x: 742, y: 40, width: 498, height: 800 }, panes: 2 }
		expect(edgeDropAt(1230, 400, [left, right], 'c')?.tabId).toBe('b')
	})
})
