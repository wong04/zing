// Krackn tests the relay viewer only by rendering real pages (Tests/render,
// Tests/encoding); these cover the ported URL mapping and rewriting offline.

import { describe, expect, it } from 'vitest'
import {
	charset,
	decodeRelayURL,
	encodeRelayURL,
	isAppLikeFailure,
	patch,
	patchText,
} from './relayedWebContent'

describe('relay URL mapping', () => {
	it('swaps only the scheme, keeping host, path and query', () => {
		expect(encodeRelayURL(new URL('https://example.com/a/style.css?v=2#x'))?.href).toBe('aiorelay://example.com/a/style.css?v=2#x')
	})
	it('round-trips', () => {
		const original = new URL('https://upload.example.org:8443/x.png')
		const relayed = encodeRelayURL(original)
		expect(relayed && decodeRelayURL(relayed)?.href).toBe(original.href)
	})
	it('a relative reference in a relayed document stays in the relay', () => {
		const page = encodeRelayURL(new URL('https://en.wikipedia.org/wiki/Main_Page'))
		expect(page && new URL('/w/load.php', page).href).toBe('aiorelay://en.wikipedia.org/w/load.php')
		expect(page && new URL('//upload.wikimedia.org/x.png', page).href).toBe('aiorelay://upload.wikimedia.org/x.png')
	})
	it('decodes only relay URLs', () => {
		expect(decodeRelayURL(new URL('https://example.com/'))).toBeUndefined()
	})
})

describe('patchText', () => {
	it('points absolute https references at the relay', () => {
		const html = '<img src="https://a.com/x.png" srcset="https://a.com/1x.png 1x, https://a.com/2x.png 2x"><a HREF=\'https://b.com/\'>'
		expect(patchText(html)).toBe(
			'<img src="aiorelay://a.com/x.png" srcset="aiorelay://a.com/1x.png 1x, aiorelay://a.com/2x.png 2x"><a HREF=\'aiorelay://b.com/\'>',
		)
	})
	it('rewrites CSS url() with and without quotes', () => {
		expect(patchText('a{background:url(https://a.com/x.png)} b{src:url( "https://a.com/f.woff2")}')).toBe(
			'a{background:url(aiorelay://a.com/x.png)} b{src:url( "aiorelay://a.com/f.woff2")}',
		)
	})
	it('removes <base>, which would re-anchor relative URLs at https', () => {
		expect(patchText('<head><BASE href="https://a.com/"><title>t</title>')).toBe('<head><title>t</title>')
	})
	it('leaves text that merely mentions https alone', () => {
		expect(patchText('<p>Visit https://a.com today</p>')).toBe('<p>Visit https://a.com today</p>')
	})
})

describe('patch', () => {
	it('keeps Latin-1 bytes intact while rewriting', () => {
		const head = new TextEncoder().encode('<p>caf')
		const tail = new TextEncoder().encode('</p><img src="https://a.com/x">')
		const body = Uint8Array.from([...head, 0xE9, ...tail])
		const patched = patch(body, 'text/html; charset=iso-8859-1')
		const expectedTail = new TextEncoder().encode('</p><img src="aiorelay://a.com/x">')
		expect(patched).toEqual(Uint8Array.from([...head, 0xE9, ...expectedTail]))
	})
	it('keeps UTF-8 multibyte characters intact while rewriting', () => {
		const body = new TextEncoder().encode('<p>naïve 日本</p><a href="https://a.com/">')
		expect(new TextDecoder().decode(patch(body, 'text/html'))).toBe('<p>naïve 日本</p><a href="aiorelay://a.com/">')
	})
	it('leaves non-HTML/CSS payloads untouched', () => {
		const body = new TextEncoder().encode('src="https://a.com/"')
		expect(patch(body, 'image/png')).toBe(body)
	})
	it('leaves UTF-16 bodies untouched rather than garbling them', () => {
		const body = Uint8Array.from([0xFF, 0xFE, 0x3C, 0x00])
		expect(patch(body, 'text/html; charset=UTF-16LE')).toBe(body)
	})
})

describe('charset', () => {
	it.each([
		['text/html; charset=EUC-JP', 'EUC-JP'],
		['text/html; charset="iso-8859-1"', 'iso-8859-1'],
		['text/plain; charset=utf-8; format=flowed', 'utf-8'],
		['text/css', 'utf-8'],
		['image/png', undefined],
	])('%s -> %s', (contentType, want) => {
		expect(charset(contentType)).toBe(want)
	})
})

describe('isAppLikeFailure', () => {
	it('lots of script and no text is a dead app', () => {
		expect(isAppLikeFailure(12, 97)).toBe(true)
	})
	it('a content page is not', () => {
		expect(isAppLikeFailure(5000, 40)).toBe(false)
		expect(isAppLikeFailure(10, 3)).toBe(false)
	})
})
