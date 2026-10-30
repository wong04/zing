// Zing's counterpart to Krackn's `UserAgent` (Features/Browser/UserAgent.swift).
//
// Krackn learned that a stale identifier makes Google serve a degraded page: a
// hard-coded "Safari 17.6" got Google Docs' unsupported-browser view, whose canvas
// drew text at a quarter of the Retina pixel count. So the version is never written
// down; here it comes from the Chromium actually running (Electron's own UA string).
// Electron adds `Electron/x` and the app's `name/version`, which make sites treat
// it as an unknown browser, so only the tokens stock Chrome sends are kept.

export class UserAgentFormatError extends Error {
	override name = 'UserAgentFormatError'
}

/** The product tokens a stock Chrome user agent is made of. */
const chromeProducts: ReadonlySet<string> = new Set(['Mozilla', 'AppleWebKit', 'Chrome', 'Safari'])

/** A product token (`Name/version`) or a parenthesised comment (`(Windows NT 10.0; Win64; x64)`). */
const uaToken = /\([^)]*\)|[^\s(]+/g

function isComment(token: string): boolean {
	return token.startsWith('(')
}

function productName(token: string): string {
	return token.split('/')[0] ?? ''
}

/**
 * Electron's default user agent with the `Electron/x` and app-name tokens removed,
 * leaving what Chrome of the same version on the same OS sends.
 */
export function chromeUserAgent(electronUserAgent: string): string {
	const tokens = electronUserAgent.match(uaToken) ?? []
	if (!tokens.some((token) => /^Chrome\/\d/.test(token))) {
		throw new UserAgentFormatError(`not a Chromium user agent: ${electronUserAgent}`)
	}
	return tokens.filter((token) => isComment(token) || chromeProducts.has(productName(token))).join(' ')
}

/**
 * What stock Chrome on Windows actually sends since User-Agent Reduction: the
 * major version only and a frozen platform token, the same on every Windows
 * machine. Sending Electron's full build number and real platform would make
 * Zing's users stand out from everyone else on Chrome — the opposite of what a
 * privacy-first browser wants.
 */
export function reducedChromeUserAgent(electronUserAgent: string): string {
	const major = /(?:^|\s)Chrome\/(\d+)[.\s]/.exec(electronUserAgent + ' ')?.[1]
	if (major === undefined) {
		throw new UserAgentFormatError(`not a Chromium user agent: ${electronUserAgent}`)
	}
	return (
		'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) ' +
		`Chrome/${major}.0.0.0 Safari/537.36`
	)
}
