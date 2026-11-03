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

/** An address with a user name or password in it would hand them to a relay. */
export class CredentialsInURLError extends BadURLError {
	override name = 'CredentialsInURLError'
	constructor(input: string) {
		super(input)
		this.message = "That address has a user name or password in it, which Zing won't send to anyone."
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
	if (url.username !== '' || url.password !== '') throw new CredentialsInURLError(input)
	return url
}

function isPrivateIPv4(text: string): boolean {
	const [a = 0, b = 0] = text.split('.').map(Number)
	return a === 0 || a === 10 || a === 127 || (a === 172 && b >= 16 && b <= 31) || (a === 192 && b === 168) || (a === 169 && b === 254)
}

function isPrivateIPv6(text: string): boolean {
	if (text === '::' || text === '::1') return true
	const mapped = /^::ffff:([0-9a-f]{1,4}):([0-9a-f]{1,4})$/.exec(text)
	if (mapped?.[1] !== undefined && mapped[2] !== undefined) {
		const high = parseInt(mapped[1], 16)
		const low = parseInt(mapped[2], 16)
		return isPrivateIPv4(`${high >> 8}.${high & 255}.${low >> 8}.${low & 255}`)
	}
	const first = text.split(':')[0] ?? ''
	return /^f[cd]/.test(first) || /^fe[89ab]/.test(first)
}

/**
 * Whether the address names something on the user's own network, judged from the text alone (no lookup): a loopback, private or
 * link-local address, localhost, a .local name, or a single-label name. Such an address must not be sent to a third party.
 */
export function isPrivateHost(url: URL): boolean {
	const host = url.hostname.toLowerCase().replace(/\.$/, '')
	if (host.startsWith('[')) return isPrivateIPv6(host.slice(1, -1))
	if (/^\d+(\.\d+){3}$/.test(host)) return isPrivateIPv4(host)
	return host === 'localhost' || host.endsWith('.localhost') || host.endsWith('.local') || !host.includes('.')
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
