// The pure parts of Krackn's Features/Security/ThreatScanner.swift. Everything
// that shelled out to a macOS tool (spctl, xattr, csrutil, fdesetup, XProtect)
// or touched the disk or network is left to the main process.

import {
	inspectContent,
	inspectFilename,
	inspectURL,
	ThreatLevel,
	verdictLevel,
	type ThreatFinding,
	type ThreatVerdict,
} from './threatModel'

export type ScanKind = 'Download' | 'Site'

/**
 * A completed scan, kept so the security page can show what it has been doing
 * rather than being a wall of toggles with no evidence behind them.
 */
export interface ScanRecord {
	readonly when: Date
	readonly subject: string
	readonly kind: ScanKind
	readonly verdict: ThreatVerdict
}

const historyLimit = 50

/** Newest first, capped so a long session doesn't grow without bound. */
export function recordScan(history: readonly ScanRecord[], entry: ScanRecord): ScanRecord[] {
	return [entry, ...history].slice(0, historyLimit)
}

function startsWith(bytes: Uint8Array, prefix: readonly number[]): boolean {
	return prefix.every((byte, index) => bytes[index] === byte)
}

/** Magic-number sniffing for the types that actually turn up in downloads. */
export function sniffType(data: Uint8Array): string | undefined {
	const b = data.subarray(0, 16)
	if (b.length < 4) return undefined
	if (startsWith(b, [0x89, 0x50, 0x4E, 0x47])) return 'png'
	if (startsWith(b, [0xFF, 0xD8, 0xFF])) return 'jpg'
	if (startsWith(b, [0x47, 0x49, 0x46, 0x38])) return 'gif'
	if (startsWith(b, [0x25, 0x50, 0x44, 0x46])) return 'pdf'
	if (startsWith(b, [0x50, 0x4B, 0x03, 0x04])) return 'zip'
	if (startsWith(b, [0x1F, 0x8B])) return 'gz'
	if (startsWith(b, [0x37, 0x7A, 0xBC, 0xAF])) return '7z'
	if (startsWith(b, [0x52, 0x61, 0x72, 0x21])) return 'rar'
	// Mach-O, in the four shapes macOS emits.
	if (
		startsWith(b, [0xCF, 0xFA, 0xED, 0xFE]) || startsWith(b, [0xCE, 0xFA, 0xED, 0xFE])
		|| startsWith(b, [0xCA, 0xFE, 0xBA, 0xBE]) || startsWith(b, [0xBE, 0xBA, 0xFE, 0xCA])
	) {
		return 'macho'
	}
	if (startsWith(b, [0x4D, 0x5A])) return 'exe'
	if (startsWith(b, [0x23, 0x21])) return 'sh' // #!
	return undefined
}

/** The extension as Foundation's `URL.pathExtension` sees it: none for `.hidden` or `name.`. */
export function pathExtension(fileName: string): string {
	const dot = fileName.lastIndexOf('.')
	if (dot <= 0 || dot === fileName.length - 1) return ''
	return fileName.slice(dot + 1)
}

/**
 * The name and header checks for a finished download. Signature and
 * Mark-of-the-Web checks are platform calls, so the caller appends those.
 */
export function inspectDownload(fileName: string, head: Uint8Array): ThreatVerdict {
	const findings = [
		...inspectFilename(fileName),
		...inspectContent(pathExtension(fileName), sniffType(head)),
	]
	return { subject: fileName, findings }
}

/**
 * Filtering resolvers answer `0.0.0.0`, an empty answer, or a sinkhole for
 * domains on their block lists — so the answer itself *is* the reputation
 * signal, no separate feed required. NXDOMAIN is also a refusal; the caller
 * sees that as a thrown `NXDomainError`.
 */
export function isRefusalAnswer(addresses: readonly string[]): boolean {
	if (addresses.length === 0) return true
	return addresses.every((address) => address === '0.0.0.0' || address === '::')
}

export function knownMaliciousDomainFinding(host: string): ThreatFinding {
	return {
		level: ThreatLevel.dangerous,
		title: 'Known-malicious domain',
		detail:
			`${host} is refused by the filtering DNS resolver, which `
			+ 'means it appears on a current malware or phishing list.',
	}
}

/**
 * Vets a URL before the browser requests it. Returns undefined to allow.
 *
 * Synchronous on purpose: it sits where the browser is waiting on the answer,
 * so it can only do pattern work. The slow, network-dependent half — asking a
 * filtering resolver whether the host is a known-bad one — runs separately
 * and fills `blockedDomains` (pass an empty set when domain blocking is off).
 * `allowedHosts` are hosts the user chose to visit anyway, so a warning isn't
 * re-shown for the rest of the session.
 */
export function checkNavigation(
	url: URL,
	allowedHosts: ReadonlySet<string>,
	blockedDomains: ReadonlySet<string>,
): ThreatVerdict | undefined {
	const scheme = url.protocol.toLowerCase()
	if (scheme !== 'http:' && scheme !== 'https:') return undefined

	const host = url.hostname.toLowerCase()
	if (allowedHosts.has(host)) return undefined

	const inspected = inspectURL(url)
	const verdict = blockedDomains.has(host)
		? { subject: inspected.subject, findings: [...inspected.findings, knownMaliciousDomainFinding(host)] }
		: inspected

	if (verdictLevel(verdict) < ThreatLevel.suspicious) return undefined
	return verdict
}
