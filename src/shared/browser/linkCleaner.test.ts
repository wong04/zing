// Port of Krackn `Tests/unit` "C. UtilityTools" link-cleaner checks, plus `stripTrackers`,
// which Krackn's suites never exercised directly.
import { describe, expect, it } from 'vitest'
import { InvalidLinkError, cleanLink, stripTrackers } from './linkCleaner'

describe('link cleaner', () => {
	const c1 = cleanLink('https://x.com/a?utm_source=n&id=7&fbclid=z')

	it('cleaner keeps real params', () => {
		expect(c1.url).toContain('id=7')
	})
	it('cleaner strips trackers', () => {
		expect(c1.removed).toHaveLength(2)
	})
	it('cleaner on clean url', () => {
		expect(cleanLink('https://x.com/a').removed).toEqual([])
	})
	it('cleaner no query', () => {
		expect(cleanLink('example.com').url).toContain('example.com')
	})
	it('cleaner all-tracker drops query', () => {
		expect(cleanLink('https://x.com/a?utm_source=n').url).not.toContain('?')
	})
	it('cleaner bad input', () => {
		expect(() => cleanLink('')).toThrow(InvalidLinkError)
	})
})

describe('link cleaner details', () => {
	it('reports what it removed, in order', () => {
		expect(c('https://x.com/a?utm_source=n&id=7&fbclid=z')).toEqual({ url: 'https://x.com/a?id=7', removed: ['utm_source', 'fbclid'] })
	})
	it('any utm_ parameter is a tracker', () => {
		expect(c('https://x.com/?utm_whatever=1&q=2').url).toBe('https://x.com/?q=2')
	})
	it('names are case-sensitive, as listed', () => {
		expect(c('https://x.com/?ICID=1&icid=2').removed).toEqual(['ICID'])
	})
	it('percent-encoded tracker names are still caught', () => {
		expect(c('https://x.com/?utm%5Fsource=1&q=2').url).toBe('https://x.com/?q=2')
	})
	it('the fragment survives', () => {
		expect(c('https://x.com/a?gclid=1#top').url).toBe('https://x.com/a#top')
	})
	it('a ? inside the fragment is not a query', () => {
		expect(c('https://x.com/a#x?utm_source=1').removed).toEqual([])
	})
	it('kept parameters are left exactly as written', () => {
		expect(c('https://x.com/?q=a%20b+c&fbclid=z&x=%E2%9C%93').url).toBe('https://x.com/?q=a%20b+c&x=%E2%9C%93')
	})
	it('pasted links lose their wrapping', () => {
		expect(c('  <https://x.com/?si=abc>  ').url).toBe('https://x.com/')
	})
	it('a link without a host is refused', () => {
		expect(() => cleanLink('file:///etc/passwd')).toThrow(InvalidLinkError)
	})
})

describe('stripping trackers on navigation', () => {
	it('removes trackers from http(s) addresses', () => {
		expect(stripTrackers('https://x.com/a?utm_source=n&id=7')).toBe('https://x.com/a?id=7')
		expect(stripTrackers('HTTP://x.com/?fbclid=1')).toBe('HTTP://x.com/')
	})
	it('leaves a clean address as it is', () => {
		expect(stripTrackers('https://x.com/a?id=7')).toBe('https://x.com/a?id=7')
	})
	it('leaves other schemes alone', () => {
		expect(stripTrackers('file:///C:/a.html?utm_source=n')).toBe('file:///C:/a.html?utm_source=n')
	})
	// Krackn ran navigations through the paste tidy-up too, which trims a trailing
	// quote: `q='hi'` became `q='hi`. A navigation is taken as it is.
	it('a quote at the end of a navigation is part of the address', () => {
		expect(stripTrackers("https://x.com/?utm_source=a&q='hi'")).toBe("https://x.com/?q='hi'")
	})
})

function c(input: string) {
	return cleanLink(input)
}
