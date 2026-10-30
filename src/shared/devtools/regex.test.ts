// Port of the regex checks in Krackn Tests/unit (section F).
import { describe, expect, it } from 'vitest'
import { RegexProblem, testRegex, testRegexIgnoringCase } from './regex'

describe('regex (Krackn Tests/unit F)', () => {
	const m = testRegex('(\\w+)@(\\w+)', 'a@b c@d')
	it('regex: match count', () => {
		expect(m).toHaveLength(2)
	})
	it('regex: groups captured', () => {
		expect(m[0]?.groups).toHaveLength(2)
	})
	it('regex: invalid pattern reported', () => {
		expect(() => testRegex('([', 'x')).toThrow(RegexProblem)
	})
})

// Zing additions.
describe('known answers', () => {
	it('reports text, range and groups', () => {
		expect(testRegex('(\\w+)@(\\w+)', 'a@b c@d')).toEqual([
			{ text: 'a@b', range: '0–3', groups: ['a', 'b'] },
			{ text: 'c@d', range: '4–7', groups: ['c', 'd'] },
		])
	})
	it('an unmatched optional group is empty, not missing', () => {
		expect(testRegex('a(x)?', 'a')[0]?.groups).toEqual([''])
	})
	it('case-insensitive matching is its own call', () => {
		expect(testRegex('abc', 'ABC abc')).toHaveLength(1)
		expect(testRegexIgnoringCase('abc', 'ABC abc')).toHaveLength(2)
	})
	it('an empty pattern matches nothing rather than everything', () => {
		expect(testRegex('', 'anything')).toEqual([])
	})
	it('zero-width matches advance instead of looping', () => {
		expect(testRegex('\\b', 'ab cd')).toHaveLength(4)
	})
})
