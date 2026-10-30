// Krackn exercises PinnedHTTPClient only against live sites (Tests/network:
// redirect chains, chunked bodies, binary integrity); these cover the ported
// parser and redirect loop offline.

import { describe, expect, it } from 'vitest'
import {
	buildRequest,
	connectionTarget,
	dechunk,
	followRedirects,
	isRedirect,
	MalformedResponseError,
	parseResponse,
	TooManyRedirectsError,
	type PinnedResponse,
} from './pinnedHTTPClient'

const ascii = new TextEncoder()
const bytes = (text: string): Uint8Array => ascii.encode(text)
const text = (data: Uint8Array): string => new TextDecoder().decode(data)

describe('parseResponse', () => {
	it('reads status, lowercased headers and body', () => {
		const response = parseResponse(bytes('HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nX-Thing:  spaced  \r\n\r\n<p>hi</p>'))
		expect(response.statusCode).toBe(200)
		expect(response.headers).toEqual({ 'content-type': 'text/html', 'x-thing': 'spaced' })
		expect(text(response.body)).toBe('<p>hi</p>')
	})
	it('leaves a non-UTF-8 body byte-for-byte intact', () => {
		const head = bytes('HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=iso-8859-1\r\n\r\n')
		const latin1 = Uint8Array.from([0x63, 0x61, 0x66, 0xE9]) // "café" in Latin-1
		const raw = new Uint8Array([...head, ...latin1])
		expect(parseResponse(raw).body).toEqual(latin1)
	})
	it('decodes a chunked body', () => {
		const raw = bytes('HTTP/1.1 200 OK\r\nTransfer-Encoding: Chunked\r\n\r\n4\r\nWiki\r\n5;ext=1\r\npedia\r\n0\r\n\r\n')
		expect(text(parseResponse(raw).body)).toBe('Wikipedia')
	})
	it('rejects a response with no header/body boundary', () => {
		expect(() => parseResponse(bytes('HTTP/1.1 200 OK\r\n'))).toThrow(MalformedResponseError)
	})
	it('rejects a status line without a numeric code', () => {
		expect(() => parseResponse(bytes('garbage\r\n\r\n'))).toThrow(MalformedResponseError)
		expect(() => parseResponse(bytes('HTTP/1.1 OK\r\n\r\n'))).toThrow(MalformedResponseError)
	})
})

describe('dechunk', () => {
	it('stops at a truncated stream without throwing', () => {
		expect(text(dechunk(bytes('a\r\n01234')))).toBe('01234')
	})
	it('stops at a size line that is not hex', () => {
		expect(text(dechunk(bytes('3\r\nabc\r\nzz\r\nignored\r\n')))).toBe('abc')
	})
})

describe('isRedirect', () => {
	const response = (statusCode: number, headers: Record<string, string>): PinnedResponse => ({ statusCode, headers, body: new Uint8Array() })
	it('needs a 3xx and a Location', () => {
		expect(isRedirect(response(301, { location: '/x' }))).toBe(true)
		expect(isRedirect(response(304, {}))).toBe(false)
		expect(isRedirect(response(200, { location: '/x' }))).toBe(false)
	})
})

describe('buildRequest', () => {
	it('keeps the encoded path and query and closes the connection', () => {
		const request = buildRequest(new URL('https://a.com/dir%20x/?q=1'), 'a.com', 'UA')
		expect(request).toBe(
			'GET /dir%20x/?q=1 HTTP/1.1\r\nHost: a.com\r\nUser-Agent: UA\r\n'
			+ 'Accept: text/html,application/xhtml+xml,*/*\r\nAccept-Encoding: identity\r\nConnection: close\r\n\r\n',
		)
	})
})

describe('connectionTarget', () => {
	it.each([
		['https://a.com/', true, 443],
		['http://a.com/', false, 80],
		['https://a.com:8443/', true, 8443],
	])('%s', (raw, isTLS, port) => {
		expect(connectionTarget(new URL(raw))).toEqual({ isTLS, port })
	})
})

describe('followRedirects', () => {
	const redirectTo = (location: string): PinnedResponse => ({ statusCode: 302, headers: { location }, body: new Uint8Array() })
	const ok: PinnedResponse = { statusCode: 200, headers: {}, body: bytes('done') }

	it('follows relative and absolute hops and reports the final URL', async () => {
		const seen: string[] = []
		const result = await followRedirects(new URL('https://a.com/start'), async (url) => {
			seen.push(url.href)
			if (url.pathname === '/start') return redirectTo('/next')
			if (url.pathname === '/next') return redirectTo('https://b.com/end')
			return ok
		})
		expect(seen).toEqual(['https://a.com/start', 'https://a.com/next', 'https://b.com/end'])
		expect(result.finalURL.href).toBe('https://b.com/end')
		expect(text(result.response.body)).toBe('done')
	})
	it('gives up after maxRedirects hops', async () => {
		let requests = 0
		const loop = followRedirects(new URL('https://a.com/'), async () => {
			requests += 1
			return redirectTo('/again')
		}, 2)
		await expect(loop).rejects.toThrow(TooManyRedirectsError)
		expect(requests).toBe(3)
	})
	it('returns the redirect itself when its Location is unusable', async () => {
		const result = await followRedirects(new URL('https://a.com/'), async () => redirectTo('http://['))
		expect(result.response.statusCode).toBe(302)
		expect(result.finalURL.href).toBe('https://a.com/')
	})
})
