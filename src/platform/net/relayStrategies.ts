import {
	EmptyBodyError,
	HTTPStatusError,
	RelayStrategy,
	validateStatus,
	waybackSnapshotURL,
} from '../../shared/unblock/relayEngine'
import { MalformedResponseError } from '../../shared/unblock/pinnedHTTPClient'
import { pinnedFetch } from './pinnedFetch'
import { systemResolve } from './probes'
import { userAgent } from './userAgent'

/**
 * Generous on purpose. A degraded origin behind a CDN can take ~20s to answer,
 * and cutting it off early reports a slow server as a blocked one.
 */
export const directTimeoutMs = 35_000
// The relay renders the page, and the archive serves from cold storage.
export const relayTimeoutMs = 30_000

export interface RelayResult {
	readonly strategy: RelayStrategy
	/** Best-effort text for display and copying; lossy for pages that aren't UTF-8. Use `rawBody` when the bytes matter. */
	readonly content: string
	/** The bytes exactly as the server sent them, to be handed to the renderer with `contentType`. */
	readonly rawBody: Uint8Array
	/** Content-Type as sent, e.g. "text/html; charset=iso-8859-1". */
	readonly contentType: string | undefined
	readonly isHTML: boolean
	readonly finalURL: URL
}

function buildResult(
	strategy: RelayStrategy,
	rawBody: Uint8Array,
	contentType: string | undefined,
	isHTML: boolean,
	finalURL: URL,
): RelayResult {
	if (rawBody.length === 0) throw new EmptyBodyError()
	return { strategy, content: new TextDecoder('utf-8').decode(rawBody), rawBody, contentType, isHTML, finalURL }
}

interface PlainResponse {
	readonly status: number
	readonly body: Uint8Array
	readonly contentType: string | undefined
	readonly finalURL: URL
}

/**
 * Ordinary fetch through the OS resolver. It goes through the same request
 * path as the pinned fetch rather than Node's `fetch`, which also caps
 * response headers at 16 KiB and failed on cloudflare.com.
 */
async function plainFetch(
	url: URL,
	signal: AbortSignal,
	timeoutMs: number,
	headers: Record<string, string>,
): Promise<PlainResponse> {
	const result = await pinnedFetch(url, signal, { timeoutMs, resolve: systemResolve, headers })
	return {
		status: result.response.statusCode,
		body: result.response.body,
		contentType: result.contentType,
		finalURL: result.finalURL,
	}
}

export async function fetchDirect(url: URL, signal: AbortSignal): Promise<RelayResult> {
	const response = await plainFetch(url, signal, directTimeoutMs, { 'User-Agent': userAgent })
	validateStatus(response.status)
	return buildResult(RelayStrategy.direct, response.body, response.contentType, true, response.finalURL)
}

export async function fetchDoHPinned(url: URL, signal: AbortSignal): Promise<RelayResult> {
	const { response, finalURL, contentType } = await pinnedFetch(url, signal, { timeoutMs: directTimeoutMs })
	if (response.statusCode < 200 || response.statusCode >= 300) throw new HTTPStatusError(response.statusCode)
	return buildResult(RelayStrategy.dohPinned, response.body, contentType, true, finalURL)
}

/**
 * Jina's reader takes the target URL as a path suffix and returns cleaned
 * markdown. It was the only general-purpose relay that survived testing on a
 * filtered network.
 */
export async function fetchReaderRelay(url: URL, signal: AbortSignal): Promise<RelayResult> {
	const response = await plainFetch(new URL(`https://r.jina.ai/${url.href}`), signal, relayTimeoutMs, {
		Accept: 'text/plain',
	})
	validateStatus(response.status)
	return buildResult(RelayStrategy.readerRelay, response.body, 'text/plain; charset=utf-8', false, url)
}

export async function fetchWayback(url: URL, signal: AbortSignal): Promise<RelayResult> {
	const lookup = new URL('https://archive.org/wayback/available')
	lookup.searchParams.set('url', url.href)
	const meta = await plainFetch(lookup, signal, directTimeoutMs, {})
	validateStatus(meta.status)

	let json: unknown
	try {
		json = JSON.parse(new TextDecoder('utf-8').decode(meta.body))
	} catch (error) {
		if (error instanceof SyntaxError) throw new MalformedResponseError()
		throw error
	}
	const snapshotURL = waybackSnapshotURL(json)

	const snapshot = await plainFetch(snapshotURL, signal, relayTimeoutMs, { 'User-Agent': userAgent })
	validateStatus(snapshot.status)
	return buildResult(RelayStrategy.wayback, snapshot.body, snapshot.contentType, true, snapshotURL)
}

export function fetchWith(strategy: RelayStrategy, url: URL, signal: AbortSignal): Promise<RelayResult> {
	switch (strategy) {
		case RelayStrategy.direct:
			return fetchDirect(url, signal)
		case RelayStrategy.dohPinned:
			return fetchDoHPinned(url, signal)
		case RelayStrategy.readerRelay:
			return fetchReaderRelay(url, signal)
		case RelayStrategy.wayback:
			return fetchWayback(url, signal)
	}
}
