import { lookup } from 'node:dns/promises'
import { abortable, throwIfCancelled } from './abort'

function isResolverFailure(error: unknown): boolean {
	return error instanceof Error && 'syscall' in error && error.syscall === 'getaddrinfo'
}

/**
 * Resolves through the OS resolver, i.e. whatever DNS server the network
 * handed out. Comparing this with a DoH answer is how tampering is detected.
 * A failed lookup is an empty answer (that is itself a signal); anything that
 * isn't a resolver failure is raised.
 */
export async function systemResolve(host: string, signal: AbortSignal): Promise<string[]> {
	throwIfCancelled(signal)
	try {
		const answers = await abortable(lookup(host, { family: 4, all: true }), signal)
		return [...new Set(answers.map((answer) => answer.address))]
	} catch (error) {
		if (isResolverFailure(error)) return []
		throw error
	}
}
