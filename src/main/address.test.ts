import { describe, expect, it } from 'vitest'
import { SEARCH_ENGINES } from '@shared/settings'
import { resolveAddress } from './address'

describe('resolveAddress', () => {
	it('goes to a real address and adds https', () => {
		expect(resolveAddress('docs.google.com', 'google')).toBe('https://docs.google.com')
	})
	it('searches for node.js rather than visiting it', () => {
		expect(resolveAddress('node.js', 'duckduckgo')).toBe('https://duckduckgo.com/?q=node.js')
	})
	it('strips tracking parameters from addresses', () => {
		expect(resolveAddress('https://example.com/a?utm_source=x&id=1', 'google')).toBe('https://example.com/a?id=1')
	})
	it('returns undefined for blank input', () => {
		expect(resolveAddress('   ', 'google')).toBeUndefined()
	})
	it('has a search URL for every engine', () => {
		for (const engine of SEARCH_ENGINES) expect(resolveAddress('two words', engine)).toContain('two%20words')
	})
})
