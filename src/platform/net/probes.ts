import { lookup } from 'node:dns/promises'
import net from 'node:net'
import tls from 'node:tls'
import { abortable, CancelledError, throwIfCancelled } from './abort'

/** A dropped-packet block shows up as an indefinite hang, so every probe has its own deadline. */
export const probeTimeoutMs = 6_000

const httpsPort = 443

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

/** True if a TCP connection to ip:443 is accepted before the deadline. */
export function tcpProbe(ip: string, signal: AbortSignal, timeoutMs = probeTimeoutMs): Promise<boolean> {
	return new Promise((resolve, reject) => {
		if (signal.aborted) return reject(new CancelledError())
		const socket = net.connect({ host: ip, port: httpsPort })
		const cleanup = (): void => {
			clearTimeout(timer)
			signal.removeEventListener('abort', onAbort)
			socket.destroy()
		}
		const finish = (reachable: boolean): void => {
			cleanup()
			resolve(reachable)
		}
		const onAbort = (): void => {
			cleanup()
			reject(new CancelledError())
		}
		const timer = setTimeout(() => finish(false), timeoutMs)
		signal.addEventListener('abort', onAbort, { once: true })
		socket.once('connect', () => finish(true))
		socket.once('error', () => finish(false))
	})
}

export interface TLSProbeResult {
	/** The handshake completed. */
	readonly handshakeCompleted: boolean
	/** The handshake completed and the certificate chains to a trusted root and is valid for the hostname. */
	readonly certificateValid: boolean
	/** Why the certificate was refused, or why the handshake failed. */
	readonly failure?: string
}

/**
 * Connects to `ip` while presenting `hostname` as the SNI, which is what a DPI
 * filter keys on, and checks the certificate against that hostname. Dialling
 * an address other than the one the hostname resolves to is the point: it asks
 * "can this address prove it is that host".
 */
export function tlsProbe(
	ip: string,
	hostname: string,
	signal: AbortSignal,
	timeoutMs = probeTimeoutMs,
): Promise<TLSProbeResult> {
	return new Promise((resolve, reject) => {
		if (signal.aborted) return reject(new CancelledError())
		// Verification failures must not abort the connection: a handshake that
		// completes with a bad certificate is a different finding from one that dies.
		const socket = tls.connect({ host: ip, port: httpsPort, servername: hostname, rejectUnauthorized: false })
		const cleanup = (): void => {
			clearTimeout(timer)
			signal.removeEventListener('abort', onAbort)
			socket.destroy()
		}
		const finish = (result: TLSProbeResult): void => {
			cleanup()
			resolve(result)
		}
		const onAbort = (): void => {
			cleanup()
			reject(new CancelledError())
		}
		const timer = setTimeout(
			() => finish({ handshakeCompleted: false, certificateValid: false, failure: 'timed out' }),
			timeoutMs,
		)
		signal.addEventListener('abort', onAbort, { once: true })
		socket.once('secureConnect', () => {
			if (socket.authorized) return finish({ handshakeCompleted: true, certificateValid: true })
			finish({ handshakeCompleted: true, certificateValid: false, failure: String(socket.authorizationError) })
		})
		socket.once('error', (error) =>
			finish({ handshakeCompleted: false, certificateValid: false, failure: error.message }),
		)
	})
}
