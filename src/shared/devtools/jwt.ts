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
const expClaim = /"exp"\s*:\s*([0-9]+)/

/** Null when the text has no `header.payload` shape at all. */
export function decodeJWT(token: string, context: DateContext): JWTParts | null {
	const [header, payload, signature] = token.trim().split('.')
	if (header === undefined || payload === undefined) return null
	const decodedPayload = decodeSegment(payload)
	return {
		header: decodeSegment(header),
		payload: decodedPayload,
		signature: signature ?? '',
		expiry: describeExpiry(decodedPayload, context),
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

function describeExpiry(payload: string, context: DateContext): string | null {
	const digits = expClaim.exec(payload)?.[1]
	if (digits === undefined) return null
	const date = new Date(Number(digits) * 1000)
	if (Number.isNaN(date.getTime())) return null
	const state = date < context.now ? '  (EXPIRED)' : '  (valid)'
	return formatMediumDateTime(date, context) + state
}
