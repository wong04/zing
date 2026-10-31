// Port of Krackn `Tests/unit` "favicons — each page's own icon, not the site's".
// The cases live in faviconChoice.vectors.json, which the C# port's tests read too.
import { describe, expect, it } from 'vitest'
import { best, largestSize, makeFaviconLink, type FaviconLinkInit } from './faviconChoice'
import vectors from './faviconChoice.vectors.json'

const choose = (links: FaviconLinkInit[]): string | null => best(links.map(makeFaviconLink))

describe('favicons — each page\'s own icon, not the site\'s', () => {
	it.each(vectors.best)('$name', ({ links, expected }) => {
		expect(choose(links)).toBe(expected)
	})
	// The bug itself: two Google products on the same host keep different icons.
	it.each(vectors.distinct)('$name', ({ a, b }) => {
		const first = choose(a)
		expect(first).not.toBeNull()
		expect(first).not.toBe(choose(b))
	})
	it.each(vectors.largestSize)('sizes "$sizes" parse', ({ sizes, expected }) => {
		expect(largestSize(sizes)).toBe(expected)
	})
})
