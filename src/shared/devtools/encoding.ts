// Port of the encoding section of Krackn Features/Utilities/UtilityTools.swift.

export class MalformedPercentEncodingError extends Error {
	override name = 'MalformedPercentEncodingError'
}

const strictBase64 = /^(?:[A-Za-z0-9+/]{4})*(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=)?$/
const utf8 = new TextEncoder()
// `fatal` so bytes that aren't UTF-8 text are refused rather than shown as U+FFFD.
const strictUtf8 = new TextDecoder('utf-8', { fatal: true })
const chunkSize = 0x8000

export function base64Encode(text: string): string {
	const bytes = utf8.encode(text)
	let binary = ''
	for (let i = 0; i < bytes.length; i += chunkSize) {
		binary += String.fromCharCode(...bytes.subarray(i, i + chunkSize))
	}
	return btoa(binary)
}

/** Null when the input isn't base64, or decodes to bytes that aren't UTF-8 text. */
export function base64Decode(text: string): string | null {
	// Tolerate URL-safe base64 and missing padding, which is what you usually
	// get when pulling a value out of a JWT or a query string.
	let normalized = text.trim().replaceAll('-', '+').replaceAll('_', '/')
	const remainder = normalized.length % 4
	if (remainder > 0) normalized += '='.repeat(4 - remainder)
	if (!strictBase64.test(normalized)) return null
	const binary = atob(normalized)
	const bytes = Uint8Array.from(binary, (c) => c.charCodeAt(0))
	return decodeUtf8(bytes)
}

/** Percent-encodes everything except RFC 3986's unreserved characters (A–Z a–z 0–9 - . _ ~). */
export function urlEncode(text: string): string {
	return encodeURIComponent(text).replace(/[!'()*]/g, (c) => `%${c.charCodeAt(0).toString(16).toUpperCase()}`)
}

export function urlDecode(text: string): string {
	try {
		return decodeURIComponent(text)
	} catch (error) {
		if (error instanceof URIError) {
			throw new MalformedPercentEncodingError(`Not valid percent-encoding: ${error.message}`, { cause: error })
		}
		throw error
	}
}

function decodeUtf8(bytes: Uint8Array): string | null {
	try {
		return strictUtf8.decode(bytes)
	} catch (error) {
		// The fatal decoder's only failure: the bytes aren't UTF-8.
		if (error instanceof TypeError) return null
		throw error
	}
}
