// Port of Krackn's Features/Security/ThreatModel.swift.

import { isRawIPv4, labelsOf, registrableDomain } from '../browser/registrableDomain'

/**
 * How serious a finding is. Ordered, so a report's overall level is just the
 * worst thing in it.
 */
export const ThreatLevel = {
	clean: 0,
	notice: 1, // worth knowing, not worth stopping for
	suspicious: 2, // looks wrong; warn loudly
	dangerous: 3, // actively hostile pattern; block by default
} as const

export type ThreatLevel = (typeof ThreatLevel)[keyof typeof ThreatLevel]

const levelLabels: Record<ThreatLevel, string> = {
	[ThreatLevel.clean]: 'Clean',
	[ThreatLevel.notice]: 'Notice',
	[ThreatLevel.suspicious]: 'Suspicious',
	[ThreatLevel.dangerous]: 'Dangerous',
}

export function threatLevelLabel(level: ThreatLevel): string {
	return levelLabels[level]
}

/**
 * One specific thing that was noticed, with the reason spelled out. The
 * reason matters: an antivirus that says "threat detected" and nothing else
 * trains people to click through it.
 */
export interface ThreatFinding {
	readonly level: ThreatLevel
	readonly title: string
	readonly detail: string
}

/** The verdict on a single subject — a URL or a file. */
export interface ThreatVerdict {
	readonly subject: string
	readonly findings: readonly ThreatFinding[]
}

export const cleanVerdict: ThreatVerdict = { subject: '', findings: [] }

export function verdictLevel(verdict: ThreatVerdict): ThreatLevel {
	return verdict.findings.reduce<ThreatLevel>(
		(worst, finding) => (finding.level > worst ? finding.level : worst),
		ThreatLevel.clean,
	)
}

export function isBlocking(verdict: ThreatVerdict): boolean {
	return verdictLevel(verdict) >= ThreatLevel.dangerous
}

function firstTitleAt(verdict: ThreatVerdict, level: ThreatLevel): string | undefined {
	return verdict.findings.find((finding) => finding.level === level)?.title
}

export function verdictSummary(verdict: ThreatVerdict): string {
	switch (verdictLevel(verdict)) {
		case ThreatLevel.clean:
			return 'Nothing suspicious found.'
		case ThreatLevel.notice:
			return verdict.findings[0]?.title ?? 'Worth a look.'
		case ThreatLevel.suspicious:
			return firstTitleAt(verdict, ThreatLevel.suspicious) ?? 'Looks suspicious.'
		case ThreatLevel.dangerous:
			return firstTitleAt(verdict, ThreatLevel.dangerous) ?? 'Blocked.'
	}
}

/** The one-line message shown after a download finishes. */
export function downloadNotice(verdict: ThreatVerdict, fileName: string): string {
	switch (verdictLevel(verdict)) {
		case ThreatLevel.clean:
		case ThreatLevel.notice:
			return `Saved ${fileName} to Downloads. Scanned — clean.`
		case ThreatLevel.suspicious:
			return `Saved ${fileName}, but it looks suspicious: ${verdictSummary(verdict)}`
		case ThreatLevel.dangerous:
			return `⚠︎ ${fileName} is dangerous: ${verdictSummary(verdict)}`
	}
}

// Pure pattern-matching over URLs and filenames.
//
// This is deliberately separate from anything that touches the disk or the
// network so every rule can be tested against a table of known-bad and
// known-good inputs. That matters more here than elsewhere in the app: a
// false positive in a security check is a feature that trains the user to
// ignore it.

// MARK: - URLs

/**
 * Top-level domains that are either abused overwhelmingly for abuse, or
 * collide with common file extensions so a filename in text becomes a
 * clickable link to somebody else's server.
 */
export const highRiskTLDs: ReadonlySet<string> = new Set([
	'zip', 'mov', // collide with filenames
	'tk', 'ml', 'ga', 'cf', 'gq', // historically free, abused
	'top', 'xyz', 'gdn', 'work', 'click', 'link',
	'country', 'kim', 'science', 'party', 'review',
])

/**
 * Brands impersonated often enough that a lookalike host is a strong
 * signal on its own.
 */
export const commonlyImpersonated: readonly string[] = [
	'apple', 'icloud', 'paypal', 'amazon', 'microsoft', 'office365',
	'google', 'gmail', 'facebook', 'instagram', 'netflix', 'coinbase',
	'binance', 'metamask', 'dhl', 'fedex', 'usps', 'hmrc', 'irs',
	'chase', 'wellsfargo', 'barclays', 'santander', 'steam', 'discord',
]

/** Words that show up in credential-harvest hostnames. */
const phishWords: ReadonlySet<string> = new Set([
	'login', 'signin', 'verify', 'verification', 'secure', 'security',
	'account', 'update', 'confirm', 'recovery', 'unlock', 'billing',
	'wallet', 'authenticate', 'support',
])

export function inspectURL(url: URL): ThreatVerdict {
	const findings: ThreatFinding[] = []
	const host = url.hostname.toLowerCase()

	// Credentials embedded in the URL are the classic way to make a link
	// read as one site while pointing at another.
	if (url.username !== '' || url.password !== '') {
		findings.push({
			level: ThreatLevel.dangerous,
			title: 'Link disguises its real destination',
			detail:
				'This URL puts a username before the host, so the part that '
				+ "looks like the site name isn't where it goes. It actually "
				+ `connects to ${host === '' ? 'an unnamed host' : host}.`,
		})
	}

	if (host !== '') {
		findings.push(...inspectHost(host, url.protocol.replace(/:$/, '').toLowerCase()))
	}

	return { subject: url.href, findings }
}

function splitLabels(host: string, separator: RegExp): string[] {
	return host.split(separator).filter((part) => part.length > 0)
}

function capitalized(word: string): string {
	return word.charAt(0).toUpperCase() + word.slice(1).toLowerCase()
}

export function inspectHost(rawHost: string, scheme: string | undefined = 'https'): ThreatFinding[] {
	const findings: ThreatFinding[] = []
	const host = rawHost.toLowerCase()
	const labels = labelsOf(host)

	// Punycode. Legitimate internationalised domains use it too, which is
	// why this is a warning and not a block — but every homograph attack
	// needs it, so it's always worth surfacing.
	if (host.includes('xn--')) {
		findings.push({
			level: ThreatLevel.suspicious,
			title: 'Address uses look-alike characters',
			detail:
				`${host} is written with non-Latin characters that can be `
				+ 'made to look identical to a familiar name. Check it letter '
				+ 'by letter before entering anything.',
		})
	}

	// A bare IP over plain HTTP is how a lot of commodity malware fetches
	// its payload. Over HTTPS it's usually just a dev box.
	if (isRawIPv4(host)) {
		findings.push({
			level: scheme === 'https' ? ThreatLevel.notice : ThreatLevel.suspicious,
			title: 'Connects to a raw IP address',
			detail:
				"There's no domain name here, so there's nothing to check a "
				+ 'certificate against and no owner to look up.',
		})
	}

	const tld = labels.at(-1)
	if (tld !== undefined && highRiskTLDs.has(tld)) {
		const collides = tld === 'zip' || tld === 'mov'
		findings.push({
			level: ThreatLevel.suspicious,
			title: `High-risk domain ending (.${tld})`,
			detail: collides
				? `The .${tld} ending is also a file extension, so text that looks `
					+ "like a filename becomes a link to somebody's server."
				: 'This ending is used disproportionately for phishing and '
					+ 'malware because registrations are free or unverified.',
		})
	}

	// Deeply nested subdomains are used to push the real domain off the
	// end of a narrow address bar.
	if (labels.length >= 6) {
		findings.push({
			level: ThreatLevel.suspicious,
			title: 'Unusually deep subdomain',
			detail:
				`${labels.length} levels deep. This is often used to bury the `
				+ 'real domain past the edge of the address bar.',
		})
	}

	// Brand impersonation: the brand stands alone as a label in the host,
	// but the domain that actually owns the site isn't the brand's.
	//
	// Matching the brand as a bare substring is far too loose, and the two
	// failures it produced are instructive: `appleinsider.com` is a real
	// publication, and `login.microsoftonline.com` is Microsoft's genuine
	// sign-in host. Both merely *contain* a brand name inside a longer
	// word. Requiring the brand to be a whole label — bounded by a dot or
	// a hyphen — keeps `apple.com.secure-login.tk` caught while leaving
	// both of those alone.
	const registrable = registrableDomain(host)
	const hostLabels = new Set(splitLabels(host, /[.-]/))
	// The brand appearing in the registrable domain means the brand owns the
	// site, whatever the subdomains look like.
	const ownerLabels = new Set(splitLabels(registrable, /[.-]/))
	const brand = commonlyImpersonated.find((name) => hostLabels.has(name) && !ownerLabels.has(name))
	if (brand !== undefined) {
		findings.push({
			level: ThreatLevel.dangerous,
			title: `Impersonates ${capitalized(brand)}`,
			detail:
				`"${brand}" appears in this address, but the site is actually `
				+ `${registrable} — not ${brand}. This is the standard shape of a `
				+ 'credential-stealing page.',
		})
	}

	// Security vocabulary in a hostname, combined with a hyphen-heavy name,
	// is a weak signal on its own but a good one in combination.
	const words = new Set(splitLabels(host, /\P{L}+/u))
	const phishHits = [...words].filter((word) => phishWords.has(word))
	const alreadyDangerous = findings.some((finding) => finding.level >= ThreatLevel.dangerous)
	if (phishHits.length >= 2 && !alreadyDangerous) {
		findings.push({
			level: ThreatLevel.suspicious,
			title: 'Hostname reads like a sign-in trap',
			detail:
				`Contains ${phishHits.sort().join(', ')}. Real `
				+ 'services put sign-in on their own domain, not in the hostname.',
		})
	}

	return findings
}

// MARK: - Files

/**
 * Types the OS will execute. A download landing as one of these deserves a
 * second look regardless of what it claims to be.
 *
 * Krackn's list is macOS-first; Zing adds the Windows types that run on a
 * double-click (`lnk`, `js`, `jse`, `cpl`) — the rest of the Windows set was
 * already there.
 */
export const executableExtensions: ReadonlySet<string> = new Set([
	'app', 'pkg', 'dmg', 'command', 'sh', 'bash', 'zsh', 'scpt', 'scptd',
	'workflow', 'action', 'kext', 'osax', 'jar', 'exe', 'scr', 'bat',
	'cmd', 'com', 'msi', 'vbs', 'vbe', 'wsf', 'ps1', 'hta', 'pif',
	'lnk', 'js', 'jse', 'cpl',
])

/** Office formats that can carry macros. */
export const macroExtensions: ReadonlySet<string> = new Set([
	'docm', 'xlsm', 'pptm', 'xlsb', 'dotm', 'xltm', 'potm', 'ppsm',
])

/**
 * Extensions people expect to be inert documents. A file with one of these
 * in the middle and an executable ending is a deliberate disguise.
 */
const documentDecoys: ReadonlySet<string> = new Set([
	'pdf', 'doc', 'docx', 'xls', 'xlsx', 'ppt', 'pptx', 'txt', 'rtf',
	'jpg', 'jpeg', 'png', 'gif', 'mp3', 'mp4', 'csv', 'zip', 'invoice',
])

const directionOverrides = /[‮‫‏]/u

export function inspectFilename(filename: string): ThreatFinding[] {
	const findings: ThreatFinding[] = []
	const components = labelsOf(filename.toLowerCase())
	const ext = components.length > 1 ? (components.at(-1) ?? '') : ''

	// Right-to-left override rewrites how the name is displayed, so
	// "photo‮gpj.exe" shows up in Explorer as "photoexe.jpg".
	if (directionOverrides.test(filename)) {
		findings.push({
			level: ThreatLevel.dangerous,
			title: 'Filename contains a text-direction override',
			detail:
				'An invisible character is reversing part of this name so it '
				+ 'displays as something other than what it is. There is no '
				+ 'legitimate reason for a download to do this.',
		})
	}

	// Double extension: a decoy document type followed by an executable.
	const inner = components.at(-2)
	if (components.length >= 3 && executableExtensions.has(ext) && inner !== undefined && documentDecoys.has(inner)) {
		findings.push({
			level: ThreatLevel.dangerous,
			title: `Disguised as a .${inner} but is a .${ext}`,
			detail:
				`The name ends ".${inner}.${ext}". It will run as a `
				+ `.${ext}; the ".${inner}" is decoration to make it look `
				+ 'like a document.',
		})
	}

	const alreadyDangerous = findings.some((finding) => finding.level >= ThreatLevel.dangerous)
	if (executableExtensions.has(ext) && !alreadyDangerous) {
		findings.push({
			level: ThreatLevel.suspicious,
			title: 'This file can run code',
			detail:
				`A .${ext} is a program, not a document. Only open it if you `
				+ "know exactly who made it and why you're getting it.",
		})
	}

	if (macroExtensions.has(ext)) {
		findings.push({
			level: ThreatLevel.suspicious,
			title: 'Document can contain macros',
			detail:
				`A .${ext} can carry embedded code that runs when opened. `
				+ 'Open it with macros disabled unless you trust the sender.',
		})
	}

	return findings
}

/** Interchangeable pairs that aren't a mismatch in any meaningful sense. */
const equivalentExtensions: readonly ReadonlySet<string>[] = [
	new Set(['jpg', 'jpeg']),
	new Set(['tif', 'tiff']),
	new Set(['htm', 'html']),
	new Set(['mp4', 'm4v']),
]

/**
 * Cross-checks the extension against what the bytes actually are. A file
 * claiming to be a PNG whose header says otherwise was renamed on purpose.
 */
export function inspectContent(ext: string, sniffed: string | undefined): ThreatFinding[] {
	if (sniffed === undefined || ext === '') return []
	const claimed = ext.toLowerCase()
	if (claimed === sniffed) return []
	if (equivalentExtensions.some((pair) => pair.has(claimed) && pair.has(sniffed))) return []
	return [{
		level: ThreatLevel.suspicious,
		title: "Contents don't match the extension",
		detail:
			`Named .${claimed} but the file's own header says it's a `
			+ `.${sniffed}. Renaming a file to disguise its type is a common `
			+ 'way to get it past a filter.',
	}]
}
