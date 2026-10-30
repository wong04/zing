// The pure parts of Krackn's Features/Unblock/DoHResolver.swift: building the
// JSON-dialect query and reading its answer. The fetch itself and the system
// resolver lookup belong to the main process.

import { isRecord } from './json'
import { NXDomainError, RelayError, ResolverStatusError, ResolverUnreachableError } from './relayEngine'

/**
 * The values match the resolvers offered in Settings exactly, so the resolver
 * chosen there is the resolver the app's own lookups use.
 */
export const DoHProvider = {
	cloudflare: 'Cloudflare',
	cloudflareSecurity: 'Cloudflare (malware blocking)',
	google: 'Google',
	quad9: 'Quad9',
} as const

export type DoHProvider = (typeof DoHProvider)[keyof typeof DoHProvider]

const endpoints: Record<DoHProvider, string> = {
	[DoHProvider.cloudflare]: 'https://cloudflare-dns.com/dns-query',
	[DoHProvider.cloudflareSecurity]: 'https://security.cloudflare-dns.com/dns-query',
	[DoHProvider.google]: 'https://dns.google/resolve',
	// Quad9 serves its JSON API on 5053; the standard port speaks wire-format only.
	[DoHProvider.quad9]: 'https://dns.quad9.net:5053/dns-query',
}

export function providerEndpoint(provider: DoHProvider): string {
	return endpoints[provider]
}

/**
 * All four speak the JSON DoH dialect, but the Cloudflare endpoints require
 * the dns-json Accept header — without it you get a 400.
 */
export function requiresJSONAcceptHeader(provider: DoHProvider): boolean {
	return provider === DoHProvider.cloudflare || provider === DoHProvider.cloudflareSecurity
}

/**
 * Whether this resolver refuses known-malicious domains. The security section
 * uses these as a reputation signal.
 */
export function filtersMalware(provider: DoHProvider): boolean {
	return provider === DoHProvider.cloudflareSecurity || provider === DoHProvider.quad9
}

export function dohQueryURL(host: string, provider: DoHProvider): URL {
	const url = new URL(providerEndpoint(provider))
	url.searchParams.set('name', host)
	url.searchParams.set('type', 'A')
	return url
}

export function dohRequestHeaders(provider: DoHProvider): Record<string, string> {
	if (!requiresJSONAcceptHeader(provider)) return {}
	return { Accept: 'application/dns-json' }
}

export class MalformedDoHResponseError extends RelayError {
	override name = 'MalformedDoHResponseError'
	constructor(readonly reason: string) {
		super(`The DNS resolver sent an answer we couldn't read: ${reason}.`)
	}
}

interface DNSAnswer {
	readonly name: string
	readonly type: number
	readonly data: string
}

function readAnswer(value: unknown): DNSAnswer {
	if (!isRecord(value)) throw new MalformedDoHResponseError('an answer is not an object')
	const { name, type, data } = value
	if (typeof name !== 'string' || typeof type !== 'number' || typeof data !== 'string') {
		throw new MalformedDoHResponseError('an answer is missing name, type or data')
	}
	return { name, type, data }
}

function readAnswers(value: unknown): DNSAnswer[] {
	if (value === undefined || value === null) return []
	if (!Array.isArray(value)) throw new MalformedDoHResponseError('Answer is not a list')
	return value.map(readAnswer)
}

/**
 * Reads a JSON DoH answer into its A records. An empty array means the name
 * resolved but has no address records; a thrown error means the lookup
 * itself failed.
 */
export function parseDoHAnswer(host: string, json: unknown): string[] {
	if (!isRecord(json) || typeof json['Status'] !== 'number') {
		throw new MalformedDoHResponseError('no Status field')
	}
	const status = json['Status']
	const answers = readAnswers(json['Answer'])
	// Status 3 is NXDOMAIN — the name genuinely doesn't exist upstream,
	// which is different from our local resolver lying about it.
	if (status === 3) throw new NXDomainError(host)
	if (status !== 0) throw new ResolverStatusError(status)
	// type 1 = A record. CNAMEs (type 5) come back in the same array and
	// must be filtered out or we'd try to connect to a hostname.
	return answers.filter((answer) => answer.type === 1).map((answer) => answer.data)
}

/** Anything but a 200 means the resolver itself couldn't be used. */
export function parseDoHResponse(host: string, provider: DoHProvider, status: number, body: string): string[] {
	if (status !== 200) throw new ResolverUnreachableError(provider)
	let json: unknown
	try {
		json = JSON.parse(body)
	} catch (error) {
		if (error instanceof SyntaxError) throw new MalformedDoHResponseError('not JSON')
		throw error
	}
	return parseDoHAnswer(host, json)
}
