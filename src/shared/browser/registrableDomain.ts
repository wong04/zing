// Port of Krackn's `ThreatHeuristics.registrableDomain` (Features/Security/ThreatModel.swift),
// which `BrowserTab.registrableDomain` calls "one definition of the same site for the whole app".
// It lives here because tab grouping is its first user; security code should import it, not copy it.

const twoPartSuffixes: ReadonlySet<string> = new Set([
	'co.uk', 'ac.uk', 'gov.uk', 'org.uk', 'me.uk', 'ltd.uk', 'plc.uk', 'nhs.uk',
	'co.jp', 'ne.jp', 'or.jp', 'ac.jp', 'go.jp',
	'co.nz', 'org.nz', 'govt.nz', 'ac.nz',
	'co.za', 'org.za', 'gov.za',
	'com.au', 'net.au', 'org.au', 'edu.au', 'gov.au',
	'com.br', 'net.br', 'org.br', 'gov.br',
	'com.cn', 'net.cn', 'org.cn', 'gov.cn', 'edu.cn',
	'com.mx', 'org.mx', 'gob.mx',
	'co.in', 'net.in', 'org.in', 'gov.in', 'ac.in',
	'co.kr', 'or.kr', 'go.kr', 'ac.kr',
	'com.tr', 'org.tr', 'gov.tr', 'edu.tr',
	'com.sg', 'edu.sg', 'gov.sg',
	'co.il', 'org.il', 'ac.il',
	'com.hk', 'org.hk', 'edu.hk', 'gov.hk',
	'com.tw', 'org.tw', 'edu.tw', 'gov.tw',
	'com.ar', 'com.co', 'com.pe', 'com.ve', 'com.ec',
	'co.id', 'or.id', 'ac.id', 'go.id',
	'co.th', 'or.th', 'ac.th', 'go.th', 'in.th',
	'com.my', 'org.my', 'edu.my', 'gov.my',
	'com.ph', 'org.ph', 'edu.ph', 'gov.ph',
	'com.vn', 'edu.vn', 'gov.vn',
	'com.pk', 'edu.pk', 'gov.pk',
	'com.ng', 'org.ng', 'gov.ng',
	'co.ke', 'or.ke', 'ac.ke',
	'com.eg', 'com.sa', 'com.ua', 'com.pl', 'co.at', 'or.at',
])

/** Swift's `Int(_:)` accepts an optional sign and ASCII digits only. */
const swiftInt = /^[+-]?[0-9]+$/

/** Swift's `split(separator:)` drops empty pieces by default. */
export function labelsOf(host: string): string[] {
	return host.split('.').filter((label) => label.length > 0)
}

export function isRawIPv4(host: string): boolean {
	const parts = labelsOf(host)
	if (parts.length !== 4) return false
	return parts.every((part) => {
		if (part.length > 3 || !swiftInt.test(part)) return false
		const value = Number(part)
		return value >= 0 && value <= 255
	})
}

/**
 * The root domain a host belongs to: `docs.google.com` and `mail.google.com`
 * are both `google.com`. Tab grouping, favicons and the phishing checks all
 * key on this, so they agree on what "the same site" means.
 */
export function registrableDomain(rawHost: string): string {
	let host = rawHost.toLowerCase()
	if (host.endsWith('.')) host = host.slice(0, -1)
	// An address isn't a domain. Cutting `192.168.1.10` to its last two
	// labels would group unrelated machines as one "site".
	if (isRawIPv4(host) || host.includes(':')) return host
	const parts = labelsOf(host)
	if (parts.length <= 2) return host
	const lastTwo = parts.slice(-2).join('.')
	if (twoPartSuffixes.has(lastTwo)) return parts.slice(-3).join('.')
	return lastTwo
}
