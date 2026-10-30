// Port of the Base64 and URL checks in Krackn Tests/unit (section C).
import { describe, expect, it } from 'vitest'
import { base64Decode, base64Encode, MalformedPercentEncodingError, urlDecode, urlEncode } from './encoding'

describe('base64 (Krackn Tests/unit C)', () => {
	it('b64 roundtrip unicode', () => {
		expect(base64Decode(base64Encode('héllo 🎉'))).toBe('héllo 🎉')
	})
	it('b64 unpadded', () => {
		expect(base64Decode('aGVsbG8')).toBe('hello')
	})
	// Krackn's check was `.map { _ in true } ?? true`, which passes whatever
	// the decoder returns. Pinned to the real answer here.
	it('b64 urlsafe', () => {
		expect(base64Decode('aGVsbG8_d29ybGQ')).toBe('hello?world')
	})
	it('b64 garbage -> nil', () => {
		expect(base64Decode('!!!not base64!!!')).toBeNull()
	})
	it('b64 empty', () => {
		expect(base64Encode('')).toBe('')
	})
	it('b64 whitespace tolerated', () => {
		expect(base64Decode('  aGVsbG8  ')).toBe('hello')
	})
})

describe('url encoding (Krackn Tests/unit C)', () => {
	it('url encode space', () => {
		expect(urlEncode('a b')).toBe('a%20b')
	})
	it('url roundtrip', () => {
		expect(urlDecode(urlEncode('a b&c=d?e'))).toBe('a b&c=d?e')
	})
	it('url encode unicode', () => {
		expect(urlEncode('café🎉')).not.toBe('')
	})
})

// Zing additions.
describe('known answers', () => {
	it('encodes RFC 4648 vectors', () => {
		expect(base64Encode('f')).toBe('Zg==')
		expect(base64Encode('fo')).toBe('Zm8=')
		expect(base64Encode('foobar')).toBe('Zm9vYmFy')
		expect(base64Encode('é')).toBe('w6k=')
	})
	it('URL-safe input with - and _ decodes', () => {
		expect(base64Decode('-_8')).toBe(null) // 0xFB 0xFF is not UTF-8 text
		expect(base64Decode('w6k')).toBe('é')
	})
	it('impossible lengths and misplaced padding are refused', () => {
		expect(base64Decode('aGVsb')).toBeNull()
		expect(base64Decode('aG=sbG8=')).toBeNull()
	})
	it('a large input encodes without blowing the call stack', () => {
		const big = 'x'.repeat(300_000)
		expect(base64Decode(base64Encode(big))).toBe(big)
	})
	it('encodes everything outside the unreserved set, ASCII or not', () => {
		expect(urlEncode("A-z0._~!*'()")).toBe('A-z0._~%21%2A%27%28%29')
		expect(urlEncode('café')).toBe('caf%C3%A9')
		expect(urlEncode('🎉')).toBe('%F0%9F%8E%89')
	})
	it('decodes percent-escapes but leaves + alone', () => {
		expect(urlDecode('caf%C3%A9+x')).toBe('café+x')
	})
	it('malformed percent-encoding is an error', () => {
		expect(() => urlDecode('100%')).toThrow(MalformedPercentEncodingError)
		expect(() => urlDecode('%C3')).toThrow(MalformedPercentEncodingError)
	})
})
