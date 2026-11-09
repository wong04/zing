import { describe, expect, it } from 'vitest'
import { canFill, dueForTombstone, entriesFor, NotAWebOriginError, originOf, recentlyDeleted, saveDecision, vaultKeepsData, type LoginSummary } from './vault'
import vectors from './vault.vectors.json'

// Vectors list only the fields a rule reads; the rest take these.
function summary(partial: Partial<LoginSummary>): LoginSummary {
	return { id: 'x', origin: 'https://a.example', username: 'u', title: '', note: '', createdAt: 0, updatedAt: 0, passwordUpdatedAt: 0, deletedAt: null, historyCount: 0, ...partial }
}

describe('origins', () => {
	it.each(vectors.origins)('$name', ({ input, origin }) => expect(originOf(input)).toBe(origin))
	it.each(vectors.notWebOrigins)('refuses $name', ({ input }) => expect(() => originOf(input)).toThrow(NotAWebOriginError))
	it('http and https are different origins', () => {
		const byName = (name: string) => vectors.origins.find((o) => o.name === name)?.origin
		expect(byName('http origin')).not.toBe(byName('https origin'))
	})
})

describe('who gets offered a login', () => {
	it.each(vectors.entriesFor)('$name', ({ summaries, topUrl, frameUrl, expected }) => {
		const hits = entriesFor(summaries.map(summary), { topUrl, frameUrl })
		expect(hits.map((s) => s.id)).toEqual(expected)
	})
	it('a cross-origin frame cannot be filled', () => {
		expect(canFill({ topUrl: 'https://shop.example/', frameUrl: 'https://accounts.example.com/embed' })).toBe(false)
	})
})

describe('whether to offer saving', () => {
	it.each(vectors.saveDecision)('$name', ({ summaries, neverSave, origin, username, passwordChanged, expected }) => {
		expect(saveDecision(summaries.map(summary), origin, username, passwordChanged, neverSave)).toBe(expected)
	})
})

describe('recently deleted', () => {
	const { now, summaries } = vectors.retention
	const all = summaries.map((s) => summary({ id: s.id, deletedAt: s.deletedAt }))
	it('holds logins deleted under 30 days ago', () => {
		const expected = summaries.filter((s) => s.recentlyDeleted).map((s) => s.id)
		expect(recentlyDeleted(all, now).map((s) => s.id)).toEqual(expected)
	})
	it.each(summaries)('$name: due for a tombstone is $dueForTombstone', ({ id, dueForTombstone: due }) => {
		const found = all.find((s) => s.id === id)
		if (!found) throw new Error(`No vector ${id}`)
		expect(dueForTombstone(found, now)).toBe(due)
	})
})

describe('the privacy banner', () => {
	it('flips once the vault holds any login, deleted ones included', () => {
		expect(vaultKeepsData([], [])).toBe(false)
		expect(vaultKeepsData([summary({ deletedAt: 1 })], [])).toBe(true)
	})
	it('flips for a site marked never-save too, though it holds no login (Ruling 39)', () => {
		expect(vaultKeepsData([], [{ origin: 'https://a.example' }])).toBe(true)
		expect(vaultKeepsData([], [])).toBe(false)
	})
})
