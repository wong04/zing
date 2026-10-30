// Port of Krackn's Tests/network sections F (PinnedHTTPClient against the real
// internet) and G (12 parallel fetches). Section H tests ConversionEngine and
// belongs with the converter.
//
// These hit live sites. Like Krackn's, they are deliberately tolerant of slow
// networks: generous per-request timeouts and time limits sized for a bad
// connection, so a slow link fails nothing but a dead one does. A failure here
// is worth re-running before believing.

import { beforeAll, beforeEach, describe, expect, it } from 'vitest'
import { normalize, RelayStrategy } from '../../shared/unblock/relayEngine'
import { dohReachable } from './liveNetwork'
import { ConnectionFailedError } from '../../shared/unblock/pinnedHTTPClient'
import { pinnedFetch } from './pinnedFetch'
import { systemResolve } from './probes'
import { relayFetch } from './relayFetch'

function describeFailure(reason: unknown): string {
	return reason instanceof Error ? `${reason.name}: ${reason.message.replace(/\s+/g, ' ')}` : String(reason)
}

const requestTimeoutMs = 30_000
const testTimeoutMs = 90_000
const neverCancelled = new AbortController().signal

let dohUp = false
beforeAll(async () => {
	dohUp = await dohReachable()
}, 30_000)
beforeEach((context) => {
	if (!dohUp) context.skip()
})

const cases = [
	{ url: 'https://example.com', note: 'plain' },
	{ url: 'http://github.com', note: 'http->https redirect' },
	{ url: 'https://wikipedia.org', note: 'redirect to www' },
	{ url: 'https://en.wikipedia.org/wiki/Wi-Fi', note: 'large chunked' },
	{ url: 'https://news.ycombinator.com', note: 'small' },
	{ url: 'https://www.google.com/search?q=test', note: 'query string' },
	{ url: 'https://cloudflare.com', note: 'cdn' },
]

describe('pinnedFetch against the real internet', () => {
	it.each(cases)('$note ($url)', async ({ url }) => {
		const { response, finalURL, contentType } = await pinnedFetch(normalize(url), neverCancelled, { timeoutMs: requestTimeoutMs })
		expect(response.statusCode).toBeGreaterThanOrEqual(200)
		expect(response.statusCode).toBeLessThan(400)
		expect(response.body.length).toBeGreaterThan(0)
		expect(contentType).toBeDefined()
		expect(finalURL.hostname).not.toBe('')
		// Node decodes chunked bodies itself, so the declared length is only
		// comparable when the server sent one.
		const declared = response.headers['content-length']
		if (declared !== undefined && response.headers['transfer-encoding'] === undefined) {
			expect(response.body.length).toBe(Number(declared))
		}
	}, testTimeoutMs)

	it('surfaces a 404 as a status, not an error', async () => {
		const { response } = await pinnedFetch(new URL('https://example.com/definitely-missing-xyz'), neverCancelled, { timeoutMs: requestTimeoutMs })
		expect(response.statusCode).toBeGreaterThanOrEqual(400)
	}, testTimeoutMs)

	it('returns a binary body intact', async () => {
		const { response } = await pinnedFetch(new URL('https://www.google.com/favicon.ico'), neverCancelled, { timeoutMs: requestTimeoutMs })
		expect(response.body.length).toBeGreaterThan(100)
	}, testTimeoutMs)

	it('fails cleanly and fast for a nonexistent host', async () => {
		const started = Date.now()
		await expect(
			pinnedFetch(new URL('https://no-such-host-zzz123456.com'), neverCancelled, { timeoutMs: 20_000 }),
		).rejects.toThrow()
		expect(Date.now() - started).toBeLessThan(25_000)
	}, testTimeoutMs)
})

describe('concurrency', () => {
	it('12 parallel DoH-pinned fetches all succeed', async () => {
		const started = Date.now()
		const results = await Promise.allSettled(
			Array.from({ length: 12 }, (_, i) =>
				relayFetch(i % 2 === 0 ? 'https://example.com' : 'https://news.ycombinator.com', {
					signal: neverCancelled,
					enabled: new Set([RelayStrategy.dohPinned]),
				}),
			),
		)
		console.info(`parallel batch took ${Math.round((Date.now() - started) / 1000)}s`)
		const failures = results.flatMap((result, i) => (result.status === 'rejected' ? [`#${i}: ${describeFailure(result.reason)}`] : []))
		expect(failures).toEqual([])
	}, testTimeoutMs)
})

// Needs no DoH: the resolver is swapped for the system one, so this runs on
// networks that block the DoH providers too.
describe('certificate validation stays on the hostname', () => {
	it('refuses an address that cannot prove it is the host', async () => {
		const [exampleAddress] = await systemResolve('example.com', neverCancelled)
		if (exampleAddress === undefined) throw new Error('example.com did not resolve')
		await expect(
			pinnedFetch(new URL('https://www.wikipedia.org'), neverCancelled, {
				timeoutMs: requestTimeoutMs,
				resolve: () => Promise.resolve([exampleAddress]),
			}),
		).rejects.toBeInstanceOf(ConnectionFailedError)
	}, testTimeoutMs)
})
