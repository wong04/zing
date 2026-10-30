// The pure parts of Krackn's Features/Unblock/RelayEngine.swift: the error
// and strategy types the rest of Unblock shares, and URL normalisation.

import { isRecord } from './json'

export class RelayError extends Error {
	override name = 'RelayError'
}

export class BadURLError extends RelayError {
	override name = 'BadURLError'
	constructor(readonly input: string) {
		super(`Not a usable URL: ${input}`)
	}
}

export class NXDomainError extends RelayError {
	override name = 'NXDomainError'
	constructor(readonly host: string) {
		super(`${host} doesn't exist upstream either — it's not your network blocking it.`)
	}
}

export class ResolverUnreachableError extends RelayError {
	override name = 'ResolverUnreachableError'
	constructor(readonly provider: string) {
		super(`Couldn't reach the ${provider} DoH resolver.`)
	}
}

export class ResolverStatusError extends RelayError {
	override name = 'ResolverStatusError'
	constructor(readonly status: number) {
		super(`DNS resolver returned status ${status}.`)
	}
}

export class AllStrategiesFailedError extends RelayError {
	override name = 'AllStrategiesFailedError'
	constructor(readonly notes: readonly string[]) {
		super('Every method failed.\n\n' + notes.join('\n'))
	}
}

export class HTTPStatusError extends RelayError {
	override name = 'HTTPStatusError'
	constructor(readonly status: number) {
		super(`Server returned HTTP ${status}.`)
	}
}

export class EmptyBodyError extends RelayError {
	override name = 'EmptyBodyError'
	constructor() {
		super('The response came back empty.')
	}
}

/**
 * How the content was obtained. Ordered roughly by fidelity — a direct fetch
 * gives you the real page, a reader relay gives you the text and nothing else.
 */
export const RelayStrategy = {
	direct: 'Direct',
	dohPinned: 'DoH + IP pinning',
	readerRelay: 'Reader relay',
	wayback: 'Wayback archive',
} as const

export type RelayStrategy = (typeof RelayStrategy)[keyof typeof RelayStrategy]

export const allRelayStrategies: readonly RelayStrategy[] = Object.values(RelayStrategy)

const explanations: Record<RelayStrategy, string> = {
	[RelayStrategy.direct]: 'Ordinary fetch. If this works the site was never blocked.',
	[RelayStrategy.dohPinned]:
		'Resolves the name over HTTPS, then connects straight to the IP while still validating the certificate against the real hostname. Defeats DNS-level blocking.',
	[RelayStrategy.readerRelay]:
		'A third-party server fetches the page and returns the text. Your network only ever sees a connection to the relay.',
	[RelayStrategy.wayback]:
		'Pulls an archived snapshot from the Internet Archive. Works even if the site is down, but the content may be old.',
}

export function strategyExplanation(strategy: RelayStrategy): string {
	return explanations[strategy]
}

/**
 * Reader and archive strategies return text, not a live document — the UI
 * renders these differently and warns that scripts/logins won't work.
 */
export function returnsLiveDocument(strategy: RelayStrategy): boolean {
	return strategy === RelayStrategy.direct || strategy === RelayStrategy.dohPinned
}

const pastedWrappers = /^[<>"']+|[<>"']+$/g

function parseURL(text: string, input: string): URL {
	try {
		return new URL(text)
	} catch (error) {
		if (error instanceof TypeError) throw new BadURLError(input)
		throw error
	}
}

/**
 * Accepts the sloppy things people actually paste: bare domains, URLs with
 * stray whitespace, links copied with surrounding angle brackets.
 */
export function normalize(input: string): URL {
	let text = input.trim().replace(pastedWrappers, '')
	if (text === '') throw new BadURLError(input)

	if (!text.includes('://')) text = 'https://' + text
	const url = parseURL(text, input)
	if (url.hostname === '') throw new BadURLError(input)
	return url
}

/** Anything outside 200..<400 is a failed fetch; redirects were already followed. */
export function validateStatus(status: number): void {
	if (status < 200 || status >= 400) throw new HTTPStatusError(status)
}

/**
 * The snapshot named by an `archive.org/wayback/available` answer.
 *
 * The API hands back http:// URLs which then redirect; upgrade to https so we
 * don't get downgraded mid-flight. Krackn replaced every "http://" in the
 * string, which also rewrote the archived page's own URL embedded in the
 * path; only the snapshot's scheme is upgraded here.
 */
export function waybackSnapshotURL(json: unknown): URL {
	const snapshots = isRecord(json) ? json['archived_snapshots'] : undefined
	const closest = isRecord(snapshots) ? snapshots['closest'] : undefined
	if (!isRecord(closest) || closest['available'] !== true || typeof closest['url'] !== 'string') {
		throw new EmptyBodyError()
	}
	const upgraded = closest['url'].replace(/^http:\/\//, 'https://')
	if (!URL.canParse(upgraded)) throw new EmptyBodyError()
	return new URL(upgraded)
}
