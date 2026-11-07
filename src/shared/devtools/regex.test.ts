// Port of the regex checks in Krackn Tests/unit (section F).
import { describe, expect, it } from 'vitest'
import { RegexProblem, regexMatchLimit, testRegex, testRegexIgnoringCase } from './regex'

describe('regex (Krackn Tests/unit F)', () => {
	const m = testRegex('(\\w+)@(\\w+)', 'a@b c@d').matches
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
		expect(testRegex('(\\w+)@(\\w+)', 'a@b c@d').matches).toEqual([
			{ text: 'a@b', range: '0–3', groups: ['a', 'b'] },
			{ text: 'c@d', range: '4–7', groups: ['c', 'd'] },
		])
	})
	it('an unmatched optional group is empty, not missing', () => {
		expect(testRegex('a(x)?', 'a').matches[0]?.groups).toEqual([''])
	})
	it('case-insensitive matching is its own call', () => {
		expect(testRegex('abc', 'ABC abc').matches).toHaveLength(1)
		expect(testRegexIgnoringCase('abc', 'ABC abc').matches).toHaveLength(2)
	})
	it('an empty pattern matches nothing rather than everything', () => {
		expect(testRegex('', 'anything')).toEqual({ matches: [], more: false })
	})
	it('zero-width matches advance instead of looping', () => {
		expect(testRegex('\\b', 'ab cd').matches).toHaveLength(4)
	})
	it(`stops at ${regexMatchLimit} matches and says there are more`, () => {
		const capped = testRegex('.', 'x'.repeat(200_000))
		expect(capped.matches).toHaveLength(regexMatchLimit)
		expect(capped.more).toBe(true)
		expect(testRegex('.', 'x'.repeat(regexMatchLimit)).more).toBe(false)
	})
})
