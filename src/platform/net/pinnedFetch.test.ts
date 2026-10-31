// Offline: a local server stands in for the site, and the resolver pins its
// hostname to 127.0.0.1, so this runs without DoH or the internet.
import { createServer, type Server } from 'node:http'
import type { AddressInfo } from 'node:net'
import { afterAll, beforeAll, describe, expect, it } from 'vitest'
import { pinnedFetch } from './pinnedFetch'
import { fetchDirect } from './relayStrategies'

let server: Server
let port = 0

beforeAll(async () => {
	server = createServer((request, response) => {
		// cloudflare.com's homepage sent more than Node's default 16 KiB of
		// headers (a long CSP plus Link preloads) and failed CI with "Header overflow".
		if (request.url === '/big-headers') response.setHeader('content-security-policy', `default-src 'self' ${'https://cdn.example '.repeat(2_500)}`)
		response.setHeader('content-type', 'text/html; charset=utf-8')
		response.end('<p>ok</p>')
	})
	await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve))
	port = (server.address() as AddressInfo).port
}, 10_000)

afterAll(() => new Promise<void>((resolve) => server.close(() => resolve())))

const toLocalhost = async (): Promise<string[]> => ['127.0.0.1']
const neverCancelled = new AbortController().signal

describe('pinnedFetch response headers', () => {
	it('accepts headers far larger than Node’s 16 KiB default, as a browser does', async () => {
		const result = await pinnedFetch(new URL(`http://site.test:${port}/big-headers`), neverCancelled, { resolve: toLocalhost })
		expect(result.response.statusCode).toBe(200)
		expect(result.response.headers['content-security-policy']!.length).toBeGreaterThan(40_000)
		expect(new TextDecoder().decode(result.response.body)).toBe('<p>ok</p>')
	})
	it('pins the hostname to the resolved address', async () => {
		const result = await pinnedFetch(new URL(`http://site.test:${port}/`), neverCancelled, { resolve: toLocalhost })
		expect(result.contentType).toBe('text/html; charset=utf-8')
	})
})

describe('the direct method (OS resolver) on the same request path', () => {
	it('also accepts oversized headers, which Node’s own fetch rejects', async () => {
		const result = await fetchDirect(new URL(`http://127.0.0.1:${port}/big-headers`), neverCancelled)
		expect(new TextDecoder().decode(result.rawBody)).toBe('<p>ok</p>')
		expect(result.contentType).toBe('text/html; charset=utf-8')
	})
})
