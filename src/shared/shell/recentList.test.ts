// Port of the recents section of Krackn Tests/panels.
import { describe, expect, it } from 'vitest'
import { removing, touching } from './recentList'

describe('recents: most recent first, no duplicates, capped at three', () => {
	let list: string[] = []
	for (const id of ['a', 'b', 'c', 'd']) list = touching(id, list)
	const capped = list
	const reused = touching('b', capped)
	const removed = removing('d', reused)

	it('capped and ordered', () => {
		expect(capped).toEqual(['d', 'c', 'b'])
	})
	it('reusing moves to the front without duplicating', () => {
		expect(reused).toEqual(['b', 'd', 'c'])
	})
	it('removing works', () => {
		expect(removed).toEqual(['b', 'c'])
	})
})
