import { mkdtempSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { emptyLibrary, favourites, type BookmarkNode, type BrowserLibrary } from '@shared/library/library'
import type { LibraryAction } from '@shared/ipc'
import vectors from '@shared/library/library.vectors.json'
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

interface Step {
	op: string
	[key: string]: unknown
}

describe.each(vectors.store)('LibraryStore: $name', (c) => {
	it('ends in the expected library with the expected writes', () => {
		vi.setSystemTime(c.now)
		const { store, writer } = newStore()
		for (const step of c.steps as Step[]) {
			switch (step.op) {
				case 'at':
					vi.setSystemTime(step['now'] as number)
					break
				case 'addFavourite':
					expect(store.addFavourite(step['title'] as string, step['url'] as string)).toBe(step['expect'])
					break
				case 'addReading':
					expect(store.addReading(step['title'] as string, step['url'] as string)).toBe(step['expect'])
					break
				case 'apply':
					expect(store.apply(step['action'] as LibraryAction) ?? null).toBe(step['expect'])
					break
				case 'urlsOf':
					expect(store.urlsOf(step['id'] as string)).toEqual(step['expect'])
					break
				case 'urlOf':
					expect(store.urlOf(step['id'] as string) ?? null).toBe(step['expect'])
					break
				case 'isFolderNode':
					expect(store.isFolderNode(step['id'] as string)).toBe(step['expect'])
					break
				case 'childrenOf':
					expect(store.childrenOf(step['id'] as string).map((n) => n.id)).toEqual(step['expect'])
					break
				default:
					throw new Error(`unknown step ${step.op}`)
			}
		}
		expect(store.snapshot()).toEqual(c.expected)
		expect(writer.schedule).toHaveBeenCalledTimes(c.saves)
	})
})

describe('LibraryStore subscribers', () => {
	it('tells subscribers what changed, until they unsubscribe', () => {
		const { store } = newStore()
		const listener = vi.fn()
		const unsubscribe = store.subscribe(listener)
		store.addReading('One', 'https://1.test/')
		expect(listener).toHaveBeenCalledWith(store.snapshot())
		unsubscribe()
		store.addReading('Two', 'https://2.test/')
		expect(listener).toHaveBeenCalledTimes(1)
	})
})

describe('moveNode', () => {
	it.each(vectors.moveNode)('$name', (c) => {
		expect(moveNode(c.nodes as BookmarkNode[], c.id, c.to) ?? null).toEqual(c.expected)
	})
})

describe('parseLibrary', () => {
	it.each(vectors.parseLibrary.valid)('$name', (c) => {
		vi.setSystemTime(vectors.parseLibrary.now)
		expect(parseLibrary(c.input)).toEqual(c.expected)
	})
	it.each(vectors.parseLibrary.invalid)('rejects $name', (c) => {
		expect(() => parseLibrary(c.input)).toThrow(TypeError)
	})
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
		writeFileSync(path, JSON.stringify(writer.schedule.mock.calls[0]![0] as BrowserLibrary))
		expect(favourites(loadLibraryFile(path))[0]).toMatchObject({ title: 'A', url: 'https://a.test/' })
	})

	it('refuses a corrupt file rather than starting empty over it', () => {
		const path = scratch()
		writeFileSync(path, '{"bookmarks": [{"id": 1}], "readingList": []}')
		expect(() => loadLibraryFile(path)).toThrow(LibraryFileError)
		writeFileSync(path, 'not json')
		expect(() => loadLibraryFile(path)).toThrow(LibraryFileError)
	})
})

describe('parseLibraryAction', () => {
	it.each(vectors.parseLibraryAction.valid)('accepts $input.type', (c) => {
		expect(parseLibraryAction(c.input)).toEqual(c.expected)
	})
	it.each(vectors.parseLibraryAction.invalid)('rejects %j', (bad) => {
		expect(() => parseLibraryAction(bad)).toThrow(InvalidLibraryActionError)
	})
})
