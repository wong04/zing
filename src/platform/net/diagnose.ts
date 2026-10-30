import {
	answersDiffer,
	BlockKind,
	classify,
	isSinkhole,
	systemProbeTarget,
	type DiagnosticReport,
} from '../../shared/unblock/blockDiagnostics'
import { NXDomainError, RelayError } from '../../shared/unblock/relayEngine'
import { CancelledError, throwIfCancelled } from './abort'
import { dohResolve } from './doh'
import { systemResolve, tcpProbe, tlsProbe } from './probes'

export interface DiagnoseOptions {
	readonly signal: AbortSignal
	readonly onProgress?: (message: string) => void
}

type PublicAnswer =
	| { readonly kind: 'addresses'; readonly addresses: string[] }
	| { readonly kind: 'nxdomain' }
	| { readonly kind: 'failed'; readonly reason: string }

async function resolvePublic(host: string, signal: AbortSignal): Promise<PublicAnswer> {
	try {
		return { kind: 'addresses', addresses: await dohResolve(host, signal) }
	} catch (error) {
		if (error instanceof NXDomainError) return { kind: 'nxdomain' }
		if (error instanceof CancelledError || !(error instanceof RelayError)) throw error
		return { kind: 'failed', reason: error.message }
	}
}

/**
 * Runs the full ladder: compare resolvers, then test TCP, then test TLS. Each
 * layer that works narrows down where the interference sits.
 */
export async function diagnose(host: string, options: DiagnoseOptions): Promise<DiagnosticReport> {
	const { signal, onProgress } = options
	throwIfCancelled(signal)
	const steps: string[] = []

	onProgress?.('Resolving via your network\'s DNS and DNS-over-HTTPS…')
	const [systemDNS, publicAnswer] = await Promise.all([systemResolve(host, signal), resolvePublic(host, signal)])
	steps.push(systemDNS.length === 0
		? 'Network DNS: no answer (possible NXDOMAIN block)'
		: `Network DNS: ${systemDNS.join(', ')}`)

	if (publicAnswer.kind === 'nxdomain') {
		steps.push("Public DoH: NXDOMAIN — the name genuinely doesn't exist")
		return { host, kind: BlockKind.siteDown, systemDNS, publicDNS: [], tcpReachable: false, tlsSucceeded: false, steps }
	}
	if (publicAnswer.kind === 'failed') {
		steps.push(`Public DoH: failed (${publicAnswer.reason})`)
		return { host, kind: BlockKind.inconclusive, systemDNS, publicDNS: [], tcpReachable: false, tlsSucceeded: false, steps }
	}

	const publicDNS = publicAnswer.addresses
	steps.push(`Public DoH: ${publicDNS.join(', ')}`)
	const [target] = publicDNS
	if (target === undefined) {
		return { host, kind: BlockKind.inconclusive, systemDNS, publicDNS, tcpReachable: false, tlsSucceeded: false, steps }
	}

	// A resolver that hands back a private/null address is the textbook
	// signature of a filtering appliance sinkholing the name.
	const sinkholed = systemDNS.some(isSinkhole)
	if (sinkholed) steps.push("Network DNS returned a sinkhole address — that's a deliberate block")
	if (answersDiffer(systemDNS, publicDNS)) steps.push('Answers differ — normal for large CDNs, so not conclusive by itself')

	let systemTLSOK = false
	const systemTarget = systemProbeTarget(systemDNS)
	if (systemTarget !== undefined) {
		onProgress?.('Checking the address your network gave you…')
		const probe = await tlsProbe(systemTarget, host, signal)
		systemTLSOK = probe.certificateValid
		steps.push(systemTLSOK
			? `Your network's address serves a valid certificate for ${host}`
			: `Your network's address failed to prove it's ${host}`)
	}

	onProgress?.(`Testing raw TCP to ${target}:443…`)
	const tcpOK = await tcpProbe(target, signal)
	steps.push(tcpOK ? 'TCP 443: reachable' : 'TCP 443: blocked or dropped')

	let tlsOK = false
	if (tcpOK) {
		onProgress?.('Testing TLS handshake with SNI…')
		tlsOK = (await tlsProbe(target, host, signal)).certificateValid
		steps.push(tlsOK ? 'TLS handshake: succeeded' : 'TLS handshake: rejected')
	}

	const kind = classify({
		systemTLSOK,
		sinkholed,
		systemAnswerEmpty: systemDNS.length === 0,
		publicTCPOK: tcpOK,
		publicTLSOK: tlsOK,
	})
	return { host, kind, systemDNS, publicDNS, tcpReachable: tcpOK, tlsSucceeded: tlsOK, steps }
}
