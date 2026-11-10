// Port of Krackn's `TrackerRules` (Features/Browser/BrowserModel.swift): the domains blockTrackers refuses
// as third-party requests. The host reads the list from Defaults/tracker-domains.json (npm run gen:host-defaults)
// and both sides are held to trackerRules.vectors.json.
import { registrableDomain } from './registrableDomain'

/**
 * A short, readable list of the trackers behind most third-party requests, not a bundled 100k-entry one: a giant
 * list can't be reviewed and goes stale the moment it ships. Ad blocking proper waits for uBlock Origin Lite.
 */
export const TRACKER_DOMAINS: readonly string[] = [
	'doubleclick.net', 'googlesyndication.com', 'googletagservices.com',
	'google-analytics.com', 'googletagmanager.com', 'adservice.google.com',
	'facebook.net', 'connect.facebook.net', 'graph.facebook.com',
	'scorecardresearch.com', 'quantserve.com', 'outbrain.com', 'taboola.com',
	'criteo.com', 'criteo.net', 'adnxs.com', 'rubiconproject.com',
	'pubmatic.com', 'openx.net', 'casalemedia.com', 'sharethrough.com',
	'amazon-adsystem.com', 'adsrvr.org', 'bluekai.com', 'krxd.net',
	'mathtag.com', 'bidswitch.net', 'smartadserver.com', 'teads.tv',
	'hotjar.com', 'fullstory.com', 'mouseflow.com', 'luckyorange.com',
	'segment.io', 'segment.com', 'mixpanel.com', 'amplitude.com',
	'branch.io', 'appsflyer.com', 'adjust.com', 'kochava.com',
	'chartbeat.com', 'parsely.com', 'newrelic.com', 'nr-data.net',
	'onesignal.com', 'pushcrew.com', 'taboolasyndication.com',
]

/** The host is a listed domain or under one. Krackn's regex had no end anchor, so it also caught `doubleclick.net.example`. */
export function isTrackerHost(rawHost: string): boolean {
	let host = rawHost.toLowerCase()
	if (host.endsWith('.')) host = host.slice(0, -1)
	return TRACKER_DOMAINS.some((domain) => host === domain || host.endsWith(`.${domain}`))
}

function hostOf(url: string): string | null {
	try {
		const parsed = new URL(url)
		return parsed.protocol === 'http:' || parsed.protocol === 'https:' ? parsed.hostname : null
	} catch {
		return null
	}
}

/**
 * Whether a request is refused: an http(s) request to a listed domain from a page on another site, as WebKit's
 * `third-party` load type had it. A site's own analytics still load, so nothing breaks the page you asked for.
 */
export function blocksTrackerRequest(requestUrl: string, pageUrl: string): boolean {
	const request = hostOf(requestUrl)
	if (request === null || !isTrackerHost(request)) return false
	const page = hostOf(pageUrl)
	if (page === null) return true
	return registrableDomain(request) !== registrableDomain(page)
}
