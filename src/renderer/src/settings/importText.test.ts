import { describe, expect, it } from 'vitest'
import { fileHelp, outcomeLine } from './importText'

const outcome = (patch: Partial<Parameters<typeof outcomeLine>[0]> = {}) => ({
	folderTitle: 'Imported from Chrome (Personal)',
	addedToBar: 0,
	addedToFolder: 0,
	skipped: [],
	...patch,
})

describe('outcomeLine', () => {
	it('says what went to the folder and the bar, and how many were skipped', () => {
		expect(outcomeLine(outcome({ addedToFolder: 412, addedToBar: 8, skipped: ['a', 'b', 'c'] }))).toBe(
			'Added 412 bookmarks to Imported from Chrome (Personal), 8 to the favourites bar; skipped 3.',
		)
	})

	it('uses the singular', () => {
		expect(outcomeLine(outcome({ addedToFolder: 1 }))).toBe('Added 1 bookmark to Imported from Chrome (Personal).')
		expect(outcomeLine(outcome({ addedToFolder: 2, addedToBar: 1 }))).toBe(
			'Added 2 bookmarks to Imported from Chrome (Personal), 1 to the favourites bar.',
		)
	})

	it('names only the bar when nothing went to the folder', () => {
		expect(outcomeLine(outcome({ addedToBar: 8 }))).toBe('Added 8 bookmarks to the favourites bar.')
		expect(outcomeLine(outcome({ addedToBar: 1 }))).toBe('Added 1 bookmark to the favourites bar.')
	})

	it('says nothing is new when nothing was added', () => {
		expect(outcomeLine(outcome())).toBe('Nothing new to import.')
		expect(outcomeLine(outcome({ skipped: ['x'] }))).toBe('Nothing new to import; skipped 1.')
	})
})

describe('fileHelp', () => {
	it('covers Dia, Safari and Firefox', () => {
		expect(fileHelp.map((h) => h.app)).toEqual(['Dia', 'Safari', 'Firefox'])
		for (const h of fileHelp) expect(h.steps).not.toBe('')
	})
})
