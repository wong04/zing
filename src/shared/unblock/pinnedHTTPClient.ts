// The pure parts of Krackn's Features/Unblock/PinnedHTTPClient.swift: the
// request it writes, the HTTP/1.1 response parser (with chunked decoding),
// and redirect following. The socket that dials a DoH-resolved address while
// advertising the real hostname as SNI is the caller's `requestOnce`.

import { BadURLError } from './relayEngine'

export interface PinnedResponse {
	readonly statusCode: number
	/** Keys lowercased so lookups don't depend on the server's casing. */
	readonly headers: Readonly<Record<string, string>>
	readonly body: Uint8Array
}

export class PinnedClientError extends Error {
	override name = 'PinnedClientError'
}

export class ConnectionFailedError extends PinnedClientError {
	override name = 'ConnectionFailedError'
	constructor(readonly reason: string) {
		super(`Connection failed: ${reason}`)
	}
}

export class TimedOutError extends PinnedClientError {
	override name = 'TimedOutError'
	constructor() {
		super('The connection timed out.')
	}
}

export class MalformedResponseError extends PinnedClientError {
	override name = 'MalformedResponseError'
	constructor() {
		super("The server sent a response we couldn't parse.")
	}
}

export class TooManyRedirectsError extends PinnedClientError {
	override name = 'TooManyRedirectsError'
	constructor() {
		super('Too many redirects.')
	}
}

export function responseLocation(response: PinnedResponse): string | undefined {
	return response.headers['location']
}

export function isRedirect(response: PinnedResponse): boolean {
	return response.statusCode >= 300 && response.statusCode < 400 && responseLocation(response) !== undefined
}

export interface ConnectionTarget {
	readonly isTLS: boolean
	readonly port: number
}

export function connectionTarget(url: URL): ConnectionTarget {
	const isTLS = url.protocol.toLowerCase() === 'https:'
	if (url.port !== '') return { isTLS, port: Number(url.port) }
	return { isTLS, port: isTLS ? 443 : 80 }
}

/**
 * `Connection: close` means the server signals the end of the body by
 * closing, so "receive until complete" is the whole read loop.
 *
 * Uses the URL's encoded path. Krackn used Foundation's `URL.path`, which is
 * percent-decoded and drops a trailing slash, so `/a%20b/` went out as `/a b`.
 */
export function buildRequest(url: URL, host: string, userAgent: string): string {
	const path = (url.pathname === '' ? '/' : url.pathname) + url.search
	return `GET ${path} HTTP/1.1\r\n`
		+ `Host: ${host}\r\n`
		+ `User-Agent: ${userAgent}\r\n`
		+ 'Accept: text/html,application/xhtml+xml,*/*\r\n'
		+ 'Accept-Encoding: identity\r\n'
		+ 'Connection: close\r\n'
		+ '\r\n'
}

const CR = 0x0D
const LF = 0x0A
const utf8 = new TextDecoder('utf-8')

function indexOfCRLF(bytes: Uint8Array, from: number): number {
	for (let i = from; i + 1 < bytes.length; i++) {
		if (bytes[i] === CR && bytes[i + 1] === LF) return i
	}
	return -1
}

function indexOfBlankLine(bytes: Uint8Array): number {
	for (let i = 0; i + 3 < bytes.length; i++) {
		if (bytes[i] === CR && bytes[i + 1] === LF && bytes[i + 2] === CR && bytes[i + 3] === LF) return i
	}
	return -1
}

function concat(parts: readonly Uint8Array[]): Uint8Array {
	const output = new Uint8Array(parts.reduce((total, part) => total + part.length, 0))
	let offset = 0
	for (const part of parts) {
		output.set(part, offset)
		offset += part.length
	}
	return output
}

const swiftInt = /^[+-]?[0-9]+$/
const swiftHexInt = /^[+-]?[0-9a-f]+$/i

/**
 * Reassembles a chunked body. Each chunk is a hex length line, the bytes,
 * then CRLF; a zero length ends the stream.
 */
export function dechunk(input: Uint8Array): Uint8Array {
	const output: Uint8Array[] = []
	let cursor = 0

	while (cursor < input.length) {
		const lineEnd = indexOfCRLF(input, cursor)
		if (lineEnd < 0) break
		const sizeLine = utf8.decode(input.subarray(cursor, lineEnd))
		// A chunk-size line may carry extensions after a semicolon.
		const sizeToken = (sizeLine.split(';')[0] ?? '').trim()
		if (!swiftHexInt.test(sizeToken)) break
		const size = parseInt(sizeToken, 16)
		if (size <= 0) break

		const chunkStart = lineEnd + 2
		const chunkEnd = Math.min(chunkStart + size, input.length)
		output.push(input.subarray(chunkStart, chunkEnd))
		// Skip the CRLF that terminates the chunk.
		cursor = Math.min(chunkEnd + 2, input.length)
	}
	return concat(output)
}

function parseHeaders(lines: readonly string[]): Record<string, string> {
	const headers: Record<string, string> = {}
	for (const line of lines) {
		const colon = line.indexOf(':')
		if (colon < 0) continue
		headers[line.slice(0, colon).trim().toLowerCase()] = line.slice(colon + 1).trim()
	}
	return headers
}

export function parseResponse(raw: Uint8Array): PinnedResponse {
	// Header/body boundary is a blank line — find it as bytes, since the body
	// may not be valid UTF-8.
	const boundary = indexOfBlankLine(raw)
	if (boundary < 0) throw new MalformedResponseError()

	const [statusLine = '', ...lines] = utf8.decode(raw.subarray(0, boundary)).split('\r\n')
	const statusCode = statusLine.split(' ').filter((part) => part !== '')[1]
	if (statusCode === undefined || !swiftInt.test(statusCode)) throw new MalformedResponseError()

	const headers = parseHeaders(lines)
	const body = raw.subarray(boundary + 4)
	const chunked = headers['transfer-encoding']?.toLowerCase().includes('chunked') === true
	return { statusCode: Number(statusCode), headers, body: chunked ? dechunk(body) : body.slice() }
}

/** Location may be relative; it resolves against the URL just fetched. */
export function redirectTarget(location: string, current: URL): URL | undefined {
	if (!URL.canParse(location, current)) return undefined
	return new URL(location, current)
}

export interface FetchResult {
	readonly response: PinnedResponse
	readonly finalURL: URL
}

/**
 * Fetches `url`, following redirects; each hop goes back through
 * `requestOnce`, which re-resolves that hop's host over DoH.
 */
export async function followRedirects(
	url: URL,
	requestOnce: (url: URL) => Promise<PinnedResponse>,
	maxRedirects = 5,
): Promise<FetchResult> {
	let current = url
	for (let hops = 0; hops <= maxRedirects; hops++) {
		if (current.hostname === '') throw new BadURLError(current.href)
		const response = await requestOnce(current)
		const location = responseLocation(response)
		if (!isRedirect(response) || location === undefined) return { response, finalURL: current }
		const next = redirectTarget(location, current)
		if (next === undefined) return { response, finalURL: current }
		current = next
	}
	throw new TooManyRedirectsError()
}
