// Port of Krackn `Tests/unit` "C. UtilityTools" link-cleaner checks, plus `stripTrackers`,
// which Krackn's suites never exercised directly.
// The cases live in linkCleaner.vectors.json, which the C# port's tests read too.
import { describe, expect, it } from 'vitest'
import { InvalidLinkError, cleanLink, stripTrackers } from './linkCleaner'
import vectors from './linkCleaner.vectors.json'

describe('link cleaner', () => {
	it.each(vectors.clean)('$name', ({ input, url, removed }) => {
		expect(cleanLink(input)).toEqual({ url, removed })
	})
	it.each(vectors.invalid)('$name', ({ input }) => {
		expect(() => cleanLink(input)).toThrow(InvalidLinkError)
	})
})

describe('stripping trackers on navigation', () => {
	// Krackn ran navigations through the paste tidy-up too, which trims a trailing
	// quote: `q='hi'` became `q='hi`. A navigation is taken as it is.
	it.each(vectors.strip)('$name', ({ input, expected }) => {
		expect(stripTrackers(input)).toBe(expected)
	})
})
