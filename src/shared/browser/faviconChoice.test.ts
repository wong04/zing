// Port of Krackn `Tests/unit` "favicons — each page's own icon, not the site's".
import { describe, expect, it } from 'vitest'
import { best, largestSize, makeFaviconLink as FL } from './faviconChoice'

describe('favicons — each page\'s own icon, not the site\'s', () => {
	it('prefers rel=icon over apple-touch-icon', () => {
		expect(
			best([
				FL({ href: 'https://x.com/touch.png', rel: 'apple-touch-icon', sizes: '180x180' }),
				FL({ href: 'https://x.com/i.png', rel: 'icon', sizes: '32x32' }),
			]),
		).toBe('https://x.com/i.png')
	})
	it('picks the size nearest 32', () => {
		expect(
			best([
				FL({ href: 'https://x.com/16.png', rel: 'icon', sizes: '16x16' }),
				FL({ href: 'https://x.com/32.png', rel: 'icon', sizes: '32x32' }),
				FL({ href: 'https://x.com/192.png', rel: 'icon', sizes: '192x192' }),
			]),
		).toBe('https://x.com/32.png')
	})
	it('an svg or \'any\' beats a tiny bitmap', () => {
		expect(
			best([
				FL({ href: 'https://x.com/16.png', rel: 'icon', sizes: '16x16' }),
				FL({ href: 'https://x.com/i.svg', rel: 'icon', type: 'image/svg+xml' }),
			]),
		).toBe('https://x.com/i.svg')
	})
	it('unlabelled icons keep the page\'s order', () => {
		expect(
			best([FL({ href: 'https://x.com/a.ico', rel: 'shortcut icon' }), FL({ href: 'https://x.com/b.ico', rel: 'icon' })]),
		).toBe('https://x.com/a.ico')
	})
	it('data: image icons are allowed', () => {
		expect(best([FL({ href: 'data:image/png;base64,AAAA', rel: 'icon' })])).toBe('data:image/png;base64,AAAA')
	})
	it('javascript: and empty hrefs are ignored', () => {
		expect(best([FL({ href: 'javascript:alert(1)', rel: 'icon' }), FL({ href: '', rel: 'icon' })])).toBeNull()
	})
	// The bug itself: two Google products on the same host keep different icons.
	it('Docs and Slides resolve to different icons', () => {
		const docsIcon = best([
			FL({ href: 'https://ssl.gstatic.com/docs/documents/images/kix-favicon-2023q4.ico', rel: 'shortcut icon' }),
		])
		const slidesIcon = best([
			FL({ href: 'https://ssl.gstatic.com/docs/presentations/images/favicon-2023q4.ico', rel: 'shortcut icon' }),
		])
		expect(docsIcon).not.toBeNull()
		expect(docsIcon).not.toBe(slidesIcon)
	})
	it('sizes parse', () => {
		expect(largestSize('16x16 32x32')).toBe(32)
		expect(largestSize('any')).toBeNull()
	})
})

// Krackn filtered on `media` inside its page script, untested; here the probe reports
// the match and `best` applies it.
describe('icons declared for the other colour scheme', () => {
	it('skips an icon whose media query does not match', () => {
		expect(
			best([
				FL({ href: 'https://github.com/light.svg', rel: 'icon', type: 'image/svg+xml', mediaMatches: false }),
				FL({ href: 'https://github.com/dark.svg', rel: 'icon', type: 'image/svg+xml', mediaMatches: true }),
			]),
		).toBe('https://github.com/dark.svg')
	})
	it('nothing but mismatched icons means no declared icon', () => {
		expect(best([FL({ href: 'https://x.com/i.png', rel: 'icon', mediaMatches: false })])).toBeNull()
	})
	it('rel and sizes are compared lowercased', () => {
		expect(
			best([
				FL({ href: 'https://x.com/touch.png', rel: 'Apple-Touch-Icon', sizes: '32X32' }),
				FL({ href: 'https://x.com/i.png', rel: 'ICON', sizes: '16X16' }),
			]),
		).toBe('https://x.com/i.png')
	})
})
