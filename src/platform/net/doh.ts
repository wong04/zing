import {
	DoHProvider,
	dohQueryURL,
	dohRequestHeaders,
	parseDoHResponse,
} from '../../shared/unblock/dohResolver'
import { NXDomainError, RelayError, ResolverUnreachableError } from '../../shared/unblock/relayEngine'
import { CancelledError, startDeadline, throwIfCancelled } from './abort'

export const dohTimeoutMs = 8_000

/** Asks one DoH provider for the host's A records. */
export async function dohResolveVia(
	host: string,
	provider: DoHProvider,
	signal: AbortSignal,
	timeoutMs = dohTimeoutMs,
): Promise<string[]> {
	throwIfCancelled(signal)
	const deadline = startDeadline(signal, timeoutMs)
	let status: number
	let body: string
	try {
		const response = await fetch(dohQueryURL(host, provider), {
			headers: dohRequestHeaders(provider),
			signal: deadline.signal,
		})
		status = response.status
		body = await response.text()
	} catch (error) {
		const translated = deadline.toError(error)
		if (translated instanceof CancelledError) throw translated
		throw new ResolverUnreachableError(provider)
	}
	return parseDoHResponse(host, provider, status, body)
}

/**
 * Cloudflare first, Google if it can't be used. NXDOMAIN is an answer, not a
 * failure, so it is raised immediately instead of asking the second provider.
 */
export async function dohResolve(host: string, signal: AbortSignal): Promise<string[]> {
	const providers = [DoHProvider.cloudflare, DoHProvider.google]
	let lastFailure: RelayError | undefined
	for (const provider of providers) {
		try {
			return await dohResolveVia(host, provider, signal)
		} catch (error) {
			if (!(error instanceof RelayError) || error instanceof NXDomainError) throw error
			if (error instanceof CancelledError) throw error
			lastFailure = error
		}
	}
	if (lastFailure === undefined) throw new ResolverUnreachableError('DoH')
	throw lastFailure
}
