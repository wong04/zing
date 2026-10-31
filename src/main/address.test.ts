import { describe, expect, it } from 'vitest'
import { SEARCH_ENGINES } from '@shared/settings'
import vectors from '@shared/browser/addressInput.vectors.json'
import { resolveAddress } from './address'

describe('resolveAddress', () => {
	it.each(vectors.resolve)('$name', ({ input, engine, expected }) => {
		expect(resolveAddress(input, engine as (typeof SEARCH_ENGINES)[number])).toBe(expected ?? undefined)
	})
	it('has a search URL for every engine', () => {
		for (const engine of SEARCH_ENGINES) expect(resolveAddress('two words', engine)).toContain('two%20words')
	})
})
