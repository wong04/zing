// The pure parts of Krackn's Features/Unblock/BlockDiagnostics.swift. The
// probes themselves (system DNS, raw TCP, TLS with SNI) are network calls and
// belong to the main process; they feed `classify`.

import { allRelayStrategies, RelayStrategy } from './relayEngine'

/** What kind of interference is happening, which determines what can defeat it. */
export const BlockKind = {
	notBlocked: 'notBlocked',
	dnsTampering: 'dnsTampering', // resolver lies; DoH fixes this
	ipBlocked: 'ipBlocked', // packets to the address are dropped; needs a tunnel
	tlsInterception: 'tlsInterception', // connection dies at the handshake; needs a tunnel
	siteDown: 'siteDown', // not the network's fault at all
	inconclusive: 'inconclusive',
} as const

export type BlockKind = (typeof BlockKind)[keyof typeof BlockKind]

const titles: Record<BlockKind, string> = {
	notBlocked: 'Not blocked',
	dnsTampering: 'DNS-level block',
	ipBlocked: 'IP-level block',
	tlsInterception: 'TLS / SNI inspection',
	siteDown: 'Site is down',
	inconclusive: 'Inconclusive',
}

const details: Record<BlockKind, string> = {
	notBlocked:
		"This site resolves and connects normally. If it isn't loading, the problem is somewhere else.",
	dnsTampering:
		"Your network's DNS server is returning a different answer than the real one. This is the most common and weakest form of blocking — DoH + IP pinning defeats it outright.",
	ipBlocked:
		'The name resolves correctly but traffic to the address is being dropped. No amount of DNS trickery fixes this; you need traffic to exit somewhere else, which means a VPN or a proxy you control.',
	tlsInterception:
		"The address accepts a connection but the TLS handshake fails. Something is reading the server name and cutting the connection. A reader relay still works here, since you're only ever connecting to the relay.",
	siteDown:
		"The hostname doesn't exist even according to public resolvers. It's not your network.",
	inconclusive: 'Results were mixed. Try the relay strategies and see what gets through.',
}

/** Which strategies are actually worth attempting given this diagnosis. */
const recommendations: Record<BlockKind, readonly RelayStrategy[]> = {
	notBlocked: [RelayStrategy.direct],
	dnsTampering: [RelayStrategy.dohPinned, RelayStrategy.readerRelay],
	ipBlocked: [RelayStrategy.readerRelay, RelayStrategy.wayback],
	tlsInterception: [RelayStrategy.readerRelay, RelayStrategy.wayback],
	siteDown: [RelayStrategy.wayback],
	inconclusive: allRelayStrategies,
}

export function blockTitle(kind: BlockKind): string {
	return titles[kind]
}

export function blockDetail(kind: BlockKind): string {
	return details[kind]
}

export function recommendedStrategies(kind: BlockKind): readonly RelayStrategy[] {
	return recommendations[kind]
}

export interface DiagnosticReport {
	readonly host: string
	readonly kind: BlockKind
	readonly systemDNS: readonly string[]
	readonly publicDNS: readonly string[]
	readonly tcpReachable: boolean
	readonly tlsSucceeded: boolean
	readonly steps: readonly string[]
}

export interface BlockObservations {
	/**
	 * The address your resolver gave proved it owns the hostname via a valid
	 * certificate. This dominates everything else.
	 */
	readonly systemTLSOK: boolean
	/** Your resolver returned a null or private address. */
	readonly sinkholed: boolean
	/** Your resolver returned nothing at all. */
	readonly systemAnswerEmpty: boolean
	/** The DoH-resolved address accepts a connection. */
	readonly publicTCPOK: boolean
	/** The DoH-resolved address completes a TLS handshake. */
	readonly publicTLSOK: boolean
}

/**
 * Turns the observations into a verdict.
 *
 * Split out as a pure function so every combination can be tested without
 * needing a genuinely blocked network to hand.
 */
export function classify(observed: BlockObservations): BlockKind {
	// Proof of identity beats every other signal: if the address you were
	// handed can present a valid certificate for the host, nothing is
	// interfering, whatever the addresses looked like.
	if (observed.systemTLSOK) return BlockKind.notBlocked

	// A completed handshake is itself proof the address is reachable, so
	// never treat the pair as contradictory.
	const reachable = observed.publicTCPOK || observed.publicTLSOK

	// Your resolver refused to answer, or answered with a dead end.
	if (observed.sinkholed || observed.systemAnswerEmpty) {
		return reachable ? BlockKind.dnsTampering : BlockKind.ipBlocked
	}

	// Your answer can't prove itself but a public one works end to end,
	// so the lie is in the resolver.
	if (observed.publicTLSOK) return BlockKind.dnsTampering

	// Reachable but the handshake dies — something is reading the server
	// name and cutting the connection.
	if (reachable) return BlockKind.tlsInterception

	// Nothing gets through to the address at all.
	return BlockKind.ipBlocked
}

function parseIPv4(ip: string): readonly [number, number] | undefined {
	const match = /^(\d{1,3})\.(\d{1,3})\.\d{1,3}\.\d{1,3}$/.exec(ip)
	if (match === null) return undefined
	return [Number(match[1]), Number(match[2])]
}

/**
 * Addresses a filter hands out when it wants a name to go nowhere: the
 * unspecified and loopback blocks, RFC 1918 private space and link-local.
 *
 * Krackn matched only `172.16.` (not the whole 172.16.0.0/12) and only the
 * single loopback address 127.0.0.1; both are covered by prefix length here.
 */
export function isSinkhole(ip: string): boolean {
	if (ip === '::' || ip === '::1') return true
	const octets = parseIPv4(ip)
	if (octets === undefined) return false
	const [first, second] = octets
	return first === 0
		|| first === 10
		|| first === 127
		|| (first === 169 && second === 254)
		|| (first === 172 && second >= 16 && second <= 31)
		|| (first === 192 && second === 168)
}

/**
 * Differing addresses prove nothing on their own. Anycast CDNs answer
 * per-resolver, so Meta, Google and Cloudflare routinely hand your resolver a
 * completely different (and completely valid) set than a public one does.
 * Treating that as tampering flags half the internet, so this only ever
 * becomes a note in the report, never an input to `classify`.
 */
export function answersDiffer(systemDNS: readonly string[], publicDNS: readonly string[]): boolean {
	if (systemDNS.length === 0) return false
	const publicSet = new Set(publicDNS)
	return systemDNS.every((address) => !publicSet.has(address))
}

/**
 * The address worth asking for a certificate: the first one your network
 * gave that isn't a sinkhole. The decisive question isn't which address you
 * got, it's whether it can prove it's the real host — a sinkhole or an
 * intercepting middlebox cannot present a valid certificate.
 */
export function systemProbeTarget(systemDNS: readonly string[]): string | undefined {
	return systemDNS.find((address) => !isSinkhole(address))
}
