// Port of `decodeJWT` from Krackn Features/Utilities/DevTools.swift.
//
// Decodes a JWT without verifying it — for inspecting claims while debugging
// auth. Verification needs the signing key, which we don't have.
import { base64Decode } from './encoding'
import { JSONProblem, prettyJSON } from './json'
import { formatMediumDateTime, type DateContext } from './timestamp'

export type JWTParts = {
	readonly header: string
	readonly payload: string
	readonly signature: string
	/** `exp` in human terms, since that's the usual reason to look. Null when there's no `exp`. */
	readonly expiry: string | null
}

const unreadable = '(unreadable)'

/** Null when the text has no `header.payload` shape at all. */
export function decodeJWT(token: string, context: DateContext): JWTParts | null {
	const [header, payload, signature] = token.trim().split('.')
	if (header === undefined || payload === undefined) return null
	return {
		header: decodeSegment(header),
		payload: decodeSegment(payload),
		signature: signature ?? '',
		expiry: describeExpiry(base64Decode(payload), context),
	}
}

function decodeSegment(segment: string): string {
	const text = base64Decode(segment)
	if (text === null) return unreadable
	// Pretty-print if it parses as JSON; otherwise show it as it came.
	try {
		return prettyJSON(text)
	} catch (error) {
		if (error instanceof JSONProblem) return text
		throw error
	}
}

/** The payload's own top-level `exp`, not one nested inside another claim. */
function expiryOf(payload: string | null): number | null {
	if (payload === null) return null
	let claims: unknown
	try {
		claims = JSON.parse(payload)
	} catch (error) {
		if (error instanceof SyntaxError) return null
		throw error
	}
	if (claims === null || typeof claims !== 'object' || !('exp' in claims)) return null
	return typeof claims.exp === 'number' ? claims.exp : null
}

function describeExpiry(payload: string | null, context: DateContext): string | null {
	const exp = expiryOf(payload)
	if (exp === null) return null
	const date = new Date(exp * 1000)
	if (Number.isNaN(date.getTime())) return null
	const state = date < context.now ? '  (EXPIRED)' : '  (valid)'
	return formatMediumDateTime(date, context) + state
}
