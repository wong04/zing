// Port of Krackn's tracking-parameter stripper: `UtilityTools.cleanLink` (the Link Cleaner
// tool, Features/Utilities/UtilityTools.swift), `RelayEngine.normalize` (its input
// tidy-up) and `BrowserTab.stripTrackers` (navigation). Both paths share one rule.

/**
 * Query parameters that exist purely to track you. Stripping them makes a
 * link shorter and stops it phoning home about where you found it.
 */
export const trackingParameters: ReadonlySet<string> = new Set([
	'utm_source', 'utm_medium', 'utm_campaign', 'utm_term', 'utm_content',
	'utm_id', 'utm_name', 'utm_cid', 'utm_reader', 'utm_social',
	'fbclid', 'gclid', 'dclid', 'gbraid', 'wbraid', 'msclkid', 'twclid',
	'igshid', 'igsh', 'mc_cid', 'mc_eid', 'yclid', '_openstat',
	'ref', 'ref_src', 'ref_url', 'referrer', 'source',
	'si', 'spm', 'scm', 'vero_id', 'vero_conv', 'wickedid',
	'oly_anon_id', 'oly_enc_id', '__s', 'trk', 'trkCampaign',
	'ICID', 'cmpid', 'campaign_id', 'ad_id', 'adset_id',
])

export interface CleanedLink {
	url: string
	/** Names of the parameters taken out, in the order they appeared. */
	removed: string[]
}

export class InvalidLinkError extends Error {
	override name = 'InvalidLinkError'
}

export function isTrackingParameter(name: string): boolean {
	return trackingParameters.has(name) || name.startsWith('utm_')
}

/** The name as Swift's `URLComponents.queryItems` reports it: percent-decoded. */
function parameterName(item: string): string {
	const raw = item.split('=')[0] ?? ''
	try {
		return decodeURIComponent(raw)
	} catch (error) {
		// A malformed escape can't spell a tracker's name; compare it as written.
		if (error instanceof URIError) return raw
		throw error
	}
}

/**
 * Removes tracking parameters, leaving every other part of the address exactly
 * as written (Swift rebuilt the query and re-encoded it; editing the text
 * doesn't need to).
 */
export function removeTrackingParameters(url: string): CleanedLink {
	const hashAt = url.indexOf('#')
	const fragment = hashAt < 0 ? '' : url.slice(hashAt)
	const beforeFragment = hashAt < 0 ? url : url.slice(0, hashAt)
	const queryAt = beforeFragment.indexOf('?')
	if (queryAt < 0) return { url, removed: [] }

	const items = beforeFragment.slice(queryAt + 1).split('&')
	const removed: string[] = []
	const kept = items.filter((item) => {
		const name = parameterName(item)
		if (!isTrackingParameter(name)) return true
		removed.push(name)
		return false
	})
	if (removed.length === 0) return { url, removed }
	const query = kept.length === 0 ? '' : '?' + kept.join('&')
	return { url: beforeFragment.slice(0, queryAt) + query + fragment, removed }
}

const wrappingPunctuation = /^[<>"']+|[<>"']+$/g

/**
 * What was pasted, as a web address: surrounding whitespace and the
 * `<>"'` a link is often wrapped in come off, and `https://` goes in front
 * when there's no scheme. Throws when there's no address with a host.
 */
export function normalizeLink(input: string): string {
	const text = input.trim().replace(wrappingPunctuation, '')
	if (text === '') throw new InvalidLinkError(`not a link: ${JSON.stringify(input)}`)
	const candidate = text.includes('://') ? text : 'https://' + text
	if (!URL.canParse(candidate) || new URL(candidate).hostname === '') {
		throw new InvalidLinkError(`not a link: ${JSON.stringify(input)}`)
	}
	return candidate
}

/** The Link Cleaner tool: a pasted link without its trackers. */
export function cleanLink(input: string): CleanedLink {
	return removeTrackingParameters(normalizeLink(input))
}

/**
 * Removes tracking parameters before the request leaves the machine.
 * Doing it at navigation time rather than on the rendered link means it
 * also covers redirects and pasted URLs. Only http(s); anything else is
 * returned untouched.
 */
export function stripTrackers(url: string): string {
	const scheme = /^([a-z][a-z0-9+.-]*):/i.exec(url)?.[1]?.toLowerCase()
	if (scheme !== 'http' && scheme !== 'https') return url
	return removeTrackingParameters(url).url
}
