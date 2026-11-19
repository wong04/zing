import { describe, expect, it } from 'vitest'
import { isPageImageDrag, pageImageAddress } from './pageImageDrag'

const transfer = (data: Record<string, string>) => ({ types: Object.keys(data), getData: (type: string) => data[type] ?? '' })

describe('page image drags', () => {
	it('are noticed by the address they offer', () => {
		expect(isPageImageDrag(['text/uri-list', 'text/html'])).toBe(true)
		expect(isPageImageDrag(['text/plain'])).toBe(false)
		expect(isPageImageDrag(['Files'])).toBe(false)
	})

	// What Chromium offers for an <img> dragged out of a page (seen with Input.setInterceptDrags).
	it('take the image src from the markup', () => {
		const dragged = transfer({ 'text/uri-list': 'http://pics.localhost/cat.png', 'text/html': '<img id="plain" src="http://pics.localhost/cat.png" width="200">' })
		expect(pageImageAddress(dragged)).toBe('http://pics.localhost/cat.png')
	})

	it('take the image, not the link, for an image inside a link', () => {
		const dragged = transfer({ 'text/uri-list': 'http://pics.localhost/elsewhere', 'text/html': '<img src="http://pics.localhost/cat.png?a=1&amp;b=2">' })
		expect(pageImageAddress(dragged)).toBe('http://pics.localhost/cat.png?a=1&b=2')
	})

	it('fall back to the first address in the uri-list', () => {
		expect(pageImageAddress(transfer({ 'text/uri-list': '# a comment\r\nhttps://example.com/a.png\r\nhttps://example.com/b.png' }))).toBe('https://example.com/a.png')
		expect(pageImageAddress(transfer({ 'text/uri-list': 'https://example.com/a.png', 'text/html': '<a href="x">words</a>' }))).toBe('https://example.com/a.png')
	})

	it('have no address without a uri-list or an image', () => {
		expect(pageImageAddress(transfer({ 'text/plain': 'words' }))).toBeUndefined()
		expect(pageImageAddress(transfer({ 'text/uri-list': '# only a comment' }))).toBeUndefined()
	})
})
