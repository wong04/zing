// The pure parts of Krackn's Features/Unblock/RelayedWebContent.swift: the
// https <-> relay-scheme mapping and the rewriting that keeps a relayed page's
// absolute links inside the relay.

/**
 * Maps real URLs onto a private scheme the browser hands to us, keeping the
 * host and path intact:
 *
 *     https://example.com/a/style.css  <->  aiorelay://example.com/a/style.css
 *
 * Preserving the structure is what makes this work. Because the *document
 * itself* is loaded from `aiorelay://`, every relative reference (`/w/load.php`)
 * and every protocol-relative one (`//upload.example.org/x.png`) resolves back
 * into our scheme automatically, with no HTML rewriting at all. Only absolute
 * `https://` links need patching.
 */
export const relayScheme = 'aiorelay'

// WHATWG URL refuses to switch a special scheme (https) to a non-special one
// through the `protocol` setter, so the scheme is swapped on the string.
function withScheme(url: URL, scheme: string): URL | undefined {
	const swapped = `${scheme}:${url.href.slice(url.protocol.length)}`
	if (!URL.canParse(swapped)) return undefined
	return new URL(swapped)
}

export function encodeRelayURL(url: URL): URL | undefined {
	return withScheme(url, relayScheme)
}

export function decodeRelayURL(url: URL): URL | undefined {
	if (url.protocol !== `${relayScheme}:`) return undefined
	return withScheme(url, 'https')
}

/**
 * Pulls `charset=` out of a Content-Type, e.g. "text/html; charset=EUC-JP".
 * Undefined for non-text lets the payload be treated as binary.
 *
 * Krackn took everything after `charset=`, so a parameter following it
 * ("charset=utf-8; format=flowed") became part of the name; this stops at `;`.
 */
export function charset(contentType: string): string | undefined {
	const match = /charset=([^;]*)/i.exec(contentType)
	const raw = match?.[1]?.replace(/^[\s"';]+|[\s"';]+$/g, '') ?? ''
	if (raw !== '') return raw
	return contentType.toLowerCase().startsWith('text/') ? 'utf-8' : undefined
}

export function isPatchable(contentType: string): boolean {
	const kind = contentType.toLowerCase()
	return kind.includes('html') || kind.includes('css')
}

const absoluteReferences: readonly RegExp[] = [
	// Attributes carrying a single URL.
	/((?:src|href|poster|data-src|srcset|action)\s*=\s*["'])https:\/\//gi,
	// CSS url(...) with or without quotes.
	/(url\(\s*["']?)https:\/\//gi,
	// Anything left inside a srcset candidate list.
	/(,\s*)https:\/\//gi,
]

/** A <base> tag would re-anchor every relative URL back at https and undo the whole arrangement. */
const baseTag = /<base[^>]*>/gi

/**
 * Relative and protocol-relative URLs come along for free, but absolute
 * `https://` references would escape back onto the blocked network — so
 * those get pointed at our scheme too.
 */
export function patchText(text: string): string {
	const pointed = absoluteReferences.reduce(
		(current, pattern) => current.replace(pattern, `$1${relayScheme}://`),
		text,
	)
	return pointed.replace(baseTag, '')
}

// Charsets whose ASCII characters aren't single ASCII bytes.
const nonASCIICompatible = /^(utf-?16|utf-?32|ucs-?2|ucs-?4)/i

function bytesToBinaryString(bytes: Uint8Array): string {
	let text = ''
	const step = 0x8000
	for (let i = 0; i < bytes.length; i += step) {
		text += String.fromCharCode(...bytes.subarray(i, i + step))
	}
	return text
}

function binaryStringToBytes(text: string): Uint8Array {
	return Uint8Array.from(text, (character) => character.charCodeAt(0))
}

/**
 * Rewrites a relayed HTML or CSS body without changing its encoding.
 *
 * The page's bytes must survive untouched apart from the rewrite: decoding
 * through the wrong charset is how Latin-1 and Windows-1252 pages got
 * corrupted. Krackn decoded with the declared charset and re-encoded; there is
 * no portable encoder for those charsets here, so this instead patches the
 * bytes one-to-one (every pattern is ASCII, and each byte maps to exactly one
 * code unit). That's lossless for every ASCII-compatible charset; UTF-16/32
 * bodies come back unpatched — a few absolute URLs escaping the relay is a
 * far smaller failure than a garbled page.
 */
export function patch(body: Uint8Array, contentType: string): Uint8Array {
	if (!isPatchable(contentType)) return body
	if (nonASCIICompatible.test(charset(contentType) ?? '')) return body
	return binaryStringToBytes(patchText(bytesToBinaryString(body)))
}

/**
 * Plenty of script, no visible text: a client-rendered app that died on a
 * browser API the relay scheme can't provide (it isn't a secure origin), so
 * the UI should say so instead of showing a dead splash screen.
 */
export function isAppLikeFailure(visibleTextLength: number, scriptCount: number): boolean {
	return visibleTextLength < 200 && scriptCount > 20
}
