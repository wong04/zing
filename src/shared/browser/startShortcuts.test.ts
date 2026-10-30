// Port of Krackn `Tests/unit` "New-tab shortcuts are yours to choose". Krackn stored the
// list in UserDefaults; here storage is the caller's, so "stored" means `encode`d and
// "nothing stored" means `decode(null)`.
import { describe, expect, it } from 'vitest'
import { decode, defaults, encode, escapeHTML, glyph, limit, make, type StartShortcut } from './startShortcuts'

describe('New-tab shortcuts are yours to choose', () => {
	it('nothing chosen yet: the built-in eight', () => {
		expect(decode(null)).toEqual(defaults)
		expect(defaults).toHaveLength(8)
	})
	it('built-ins keep their symbols', () => {
		expect(glyph(defaults[0] as StartShortcut)).toBe('◱')
	})

	const apple = make('', 'apple.com')
	const docs = make('  My Docs ', 'docs.google.com/document/u/0/')

	it('a bare address becomes https', () => {
		expect(apple?.url).toBe('https://apple.com')
	})
	it('and is named after the site', () => {
		expect(apple?.title).toBe('Apple')
	})
	it('its symbol is its initial', () => {
		expect(apple && glyph(apple)).toBe('A')
	})
	it('a given name is kept, trimmed', () => {
		expect(docs?.title).toBe('My Docs')
	})
	it('paths survive', () => {
		expect(docs?.url).toBe('https://docs.google.com/document/u/0/')
	})
	it('words are not an address', () => {
		expect(make('x', 'best pizza')).toBeNull()
	})
	it('javascript: is refused', () => {
		expect(make('x', 'javascript:alert(1)')).toBeNull()
	})
	it('file: is refused', () => {
		expect(make('x', 'file:///etc/passwd')).toBeNull()
	})

	it('a chosen list round-trips', () => {
		const chosen = [apple, docs] as StartShortcut[]
		expect(decode(encode(chosen))).toEqual(chosen)
	})
	it('an empty list stays empty (not the defaults)', () => {
		expect(decode(encode([]))).toEqual([])
	})
	it('restore defaults', () => {
		// Krackn's resetToDefaults removes the stored value.
		expect(decode(null)).toEqual(defaults)
	})

	// Stored data is never trusted into a page's hrefs.
	it('stored non-web addresses are dropped', () => {
		const tampered =
			'[{"title":"ok","url":"https://ok.com"},{"title":"x","url":"javascript:alert(1)"},{"title":"y","url":"data:text/html,hi"}]'
		expect(decode(tampered).map((s) => s.url)).toEqual(['https://ok.com'])
	})
	it('unreadable data falls back to the built-ins', () => {
		expect(decode('{nope')).toEqual(defaults)
	})
	it(`capped at ${limit}`, () => {
		const many = Array.from({ length: 30 }, (_, i) => ({ title: `s${i}`, url: `https://s${i}.com` }))
		expect(decode(encode(many))).toHaveLength(limit)
	})
	it('names are escaped for the page', () => {
		expect(escapeHTML(`<script>"x" & 'y'</script>`)).toBe('&lt;script&gt;&quot;x&quot; &amp; &#39;y&#39;&lt;/script&gt;')
	})
})

// Not in Krackn's suite: cases the TypeScript port has to get right on its own.
describe('shortcut details', () => {
	it('a name keeps the host as typed, not punycode', () => {
		expect(make('', 'bücher.de')?.title).toBe('Bücher')
	})
	it('www. is dropped from the name', () => {
		expect(make('', 'www.apple.com')?.title).toBe('Apple')
	})
	it('a well-formed list of the wrong shape falls back to the built-ins', () => {
		expect(decode('[{"title":1,"url":"https://ok.com"}]')).toEqual(defaults)
	})
})
