import http from 'node:http'
import https from 'node:https'
import net from 'node:net'
import {
	connectionTarget,
	followRedirects,
	type FetchResult,
	type PinnedResponse,
} from '../../shared/unblock/pinnedHTTPClient'
import { startDeadline, throwIfCancelled } from './abort'
import { dohResolve } from './doh'
import { NoAddressError } from './errors'
import { userAgent } from './userAgent'

export const pinnedTimeoutMs = 20_000

export interface PinnedFetchOptions {
	/** Per hop. */
	readonly timeoutMs?: number
	/** Where addresses come from; DoH unless a test supplies another. */
	readonly resolve?: AddressResolver
	/** Sent on every hop, on top of the defaults (a Zing user agent, HTML Accept, no compression). */
	readonly headers?: Readonly<Record<string, string>>
}

export interface PinnedFetchResult extends FetchResult {
	readonly contentType: string | undefined
}

function bareHostname(url: URL): string {
	return url.hostname.replace(/^\[|\]$/g, '')
}

/** Always answers with `address`, so the socket goes there while TLS still sees the hostname. */
function pinnedLookup(address: string): net.LookupFunction {
	return (_hostname, options, callback) => {
		const family = net.isIPv6(address) ? 6 : 4
		if (options.all === true) return callback(null, [{ address, family }])
		callback(null, address, family)
	}
}

/** Chromium's own limit on a response's headers. */
const CHROMIUM_MAX_HEADER_BYTES = 256 * 1024

export type AddressResolver = (host: string, signal: AbortSignal) => Promise<string[]>

async function addressFor(url: URL, signal: AbortSignal, resolve: AddressResolver): Promise<string> {
	const host = bareHostname(url)
	if (net.isIP(host) !== 0) return host
	const [address] = await resolve(host, signal)
	if (address === undefined) throw new NoAddressError(host)
	return address
}

function lowercaseHeaders(headers: http.IncomingHttpHeaders): Record<string, string> {
	const result: Record<string, string> = {}
	for (const [name, value] of Object.entries(headers)) {
		if (value === undefined) continue
		result[name.toLowerCase()] = Array.isArray(value) ? value.join(', ') : value
	}
	return result
}

function requestOnce(
	url: URL,
	signal: AbortSignal,
	timeoutMs: number,
	resolve: AddressResolver,
	headers: Readonly<Record<string, string>>,
): Promise<PinnedResponse> {
	return new Promise((done, reject) => {
		const deadline = startDeadline(signal, timeoutMs)
		const fail = (error: unknown): void => reject(deadline.toError(error))
		addressFor(url, deadline.signal, resolve).then((address) => {
			const { isTLS, port } = connectionTarget(url)
			const transport = isTLS ? https : http
			const request = transport.request({
				host: bareHostname(url),
				port,
				path: (url.pathname === '' ? '/' : url.pathname) + url.search,
				method: 'GET',
				lookup: pinnedLookup(address),
				// Node's default 16 KiB rejected cloudflare.com ("Header overflow");
				// a real page has to load here just as it does in the browser.
				maxHeaderSize: CHROMIUM_MAX_HEADER_BYTES,
				agent: false,
				signal: deadline.signal,
				headers: {
					'User-Agent': userAgent,
					Accept: 'text/html,application/xhtml+xml,*/*',
					'Accept-Encoding': 'identity',
					Connection: 'close',
					...headers,
				},
			}, (response) => {
				const chunks: Buffer[] = []
				response.on('data', (chunk: Buffer) => chunks.push(chunk))
				response.once('error', fail)
				response.once('aborted', () => fail(new Error('connection closed before the response finished')))
				response.once('end', () => done({
					statusCode: response.statusCode ?? 0,
					headers: lowercaseHeaders(response.headers),
					body: new Uint8Array(Buffer.concat(chunks)),
				}))
			})
			request.once('error', fail)
			request.end()
		}, fail)
	})
}

/**
 * GET `url` from its DoH-resolved address. SNI and certificate validation stay
 * on the hostname, so a filter that intercepts the connection fails
 * validation and the fetch is refused. Redirects are followed and each hop
 * re-resolves its own host. The body stays raw bytes: decoding to a string
 * here is what corrupts Latin-1 pages.
 */
export async function pinnedFetch(
	url: URL,
	signal: AbortSignal,
	options: PinnedFetchOptions = {},
): Promise<PinnedFetchResult> {
	throwIfCancelled(signal)
	const { timeoutMs = pinnedTimeoutMs, resolve = dohResolve, headers = {} } = options
	const result = await followRedirects(url, (hop) => requestOnce(hop, signal, timeoutMs, resolve, headers))
	return { ...result, contentType: result.response.headers['content-type'] }
}
