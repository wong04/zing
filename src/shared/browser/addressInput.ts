// Port of Krackn's `AddressInput` (Features/Browser/AddressInput.swift).

/**
 * `url` is what was typed (with `https://` or `http://` put in front when it
 * had no scheme), unaltered: like Swift's `URL.absoluteString`, it isn't
 * normalised, so `Apple.com` stays `https://Apple.com`.
 */
export type AddressInputResult = { kind: 'url'; url: string } | { kind: 'search'; text: string }

const swiftInt = /^[+-]?[0-9]+$/
const allDigits = /^\p{N}+$/u
const hostLabel = /^[\p{L}\p{N}-]+$/u
const whitespace = /\s/u

function urlOrSearch(candidate: string, text: string): AddressInputResult {
	return URL.canParse(candidate) ? { kind: 'url', url: candidate } : { kind: 'search', text }
}

export function isIPv4(host: string): boolean {
	const parts = host.split('.')
	return (
		parts.length === 4 &&
		parts.every((part) => {
			if (part.length > 3 || !swiftInt.test(part)) return false
			const value = Number(part)
			return value >= 0 && value <= 255
		})
	)
}

/**
 * Decides whether what was typed is a place to go or something to search for.
 * Null when there is nothing but whitespace.
 *
 * One rule for every box that takes either — the address bar and the
 * new-tab page's search field. The new-tab field used to send everything to
 * the search engine, so typing `apple.com` searched for "apple.com"; and the
 * address bar's old test was "contains a dot", which sent `node.js` off to a
 * website that doesn't exist. Now: a scheme, `localhost`, an IP address, a
 * port, or a hostname ending in a real top-level domain means "go there";
 * anything else is a search. A bare name like `docs.google.com` or
 * `apple.com/iphone` needs no `https://`.
 */
export function classify(raw: string): AddressInputResult | null {
	const text = raw.trim()
	if (text === '') return null
	// Spaces mean words, not an address.
	if (whitespace.test(text)) return { kind: 'search', text }

	// An explicit scheme — https://, file://, the app's own pages.
	if (text.includes('://')) return urlOrSearch(text, text)
	const lower = text.toLowerCase()
	if (lower.startsWith('about:') || lower.startsWith('mailto:')) return urlOrSearch(text, text)

	// The host is everything before the first /, ? or #; a port may follow.
	const hostEnd = text.search(/[/?#]/)
	let host = (hostEnd < 0 ? text : text.slice(0, hostEnd)).toLowerCase()
	let hasPort = false
	const colon = host.lastIndexOf(':')
	if (colon >= 0) {
		const port = host.slice(colon + 1)
		if (!allDigits.test(port)) return { kind: 'search', text }
		host = host.slice(0, colon)
		hasPort = true
	}

	// Local and numeric addresses are plain http, as browsers treat them.
	if (host === 'localhost' || isIPv4(host)) return urlOrSearch('http://' + text, text)

	const labels = host.split('.')
	const tld = labels.at(-1) ?? ''
	if (labels.length < 2 || !labels.every((label) => hostLabel.test(label))) return { kind: 'search', text }
	// A port is a strong enough signal on its own (`myserver.lan:8080`).
	if (!hasPort && !isTopLevelDomain(tld)) return { kind: 'search', text }
	return urlOrSearch('https://' + text, text)
}

/**
 * The real country-code domains, plus the generic ones people type. Not
 * the full IANA list — 1,400 entries and growing — but enough that a real
 * address is never searched, while `node.js`, `file.txt` and `index.html`
 * still are. (Accepting *any* two letters looked tidy, and sent `node.js`
 * to a website: `.js` isn't a country.)
 */
export function isTopLevelDomain(tld: string): boolean {
	const t = tld.toLowerCase()
	return countryTLDs.has(t) || genericTLDs.has(t)
}

export const countryTLDs: ReadonlySet<string> = new Set(
	`
	ac ad ae af ag ai al am ao aq ar as at au aw ax az ba bb bd be bf bg bh bi bj bm bn bo
	br bs bt bw by bz ca cc cd cf cg ch ci ck cl cm cn co cr cu cv cw cx cy cz de dj dk dm
	do dz ec ee eg er es et eu fi fj fk fm fo fr ga gd ge gf gg gh gi gl gm gn gp gq gr gs
	gt gu gw gy hk hm hn hr ht hu id ie il im in io iq ir is it je jm jo jp ke kg kh ki km
	kn kp kr kw ky kz la lb lc li lk lr ls lt lu lv ly ma mc md me mg mh mk ml mm mn mo mp
	mq mr ms mt mu mv mw mx my mz na nc ne nf ng ni nl no np nr nu nz om pa pe pf pg ph pk
	pl pm pn pr ps pt pw py qa re ro rs ru rw sa sb sc sd se sg sh si sk sl sm sn so sr ss
	st su sv sx sy sz tc td tf tg th tj tk tl tm tn to tr tt tv tw tz ua ug uk us uy uz va
	vc ve vg vi vn vu wf ws ye yt za zm zw
	`
		.split(/\s+/)
		.filter((code) => code.length > 0),
)

export const genericTLDs: ReadonlySet<string> = new Set([
	'com', 'net', 'org', 'edu', 'gov', 'mil', 'int', 'info', 'biz', 'name', 'pro',
	'io', 'ai', 'app', 'dev', 'page', 'cloud', 'tech', 'online', 'site', 'website',
	'store', 'shop', 'blog', 'news', 'live', 'art', 'design', 'xyz', 'top', 'club',
	'today', 'world', 'space', 'studio', 'media', 'network', 'systems', 'software',
	'digital', 'agency', 'email', 'solutions', 'services', 'academy', 'school',
	'college', 'university', 'museum', 'travel', 'health', 'law', 'finance', 'bank',
	'money', 'games', 'game', 'fun', 'wiki', 'social', 'chat', 'video', 'music',
	'photo', 'photos', 'gallery', 'film', 'tv', 'radio', 'zone', 'one', 'link',
	'click', 'help', 'support', 'guide', 'tips', 'how', 'new', 'plus', 'inc',
	'ltd', 'llc', 'gmbh', 'group', 'global', 'earth', 'city', 'land', 'moe',
	'dog', 'cat', 'eco', 'green', 'lol', 'wtf', 'rocks', 'ninja', 'codes',
	'google', 'apple', 'microsoft', 'amazon', 'youtube', 'gle', 'goog', 'mozilla',
	'local', 'lan', 'home', 'internal', 'test', 'localhost', 'onion', 'arpa', 'coop', 'aero',
])
