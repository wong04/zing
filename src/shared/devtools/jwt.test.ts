// Port of the JWT checks in Krackn Tests/unit (section F).
import { describe, expect, it } from 'vitest'
import { base64Encode } from './encoding'
import { decodeJWT } from './jwt'

const context = { now: new Date('2025-01-01T00:00:00Z'), locale: 'en-US', timeZone: 'UTC' }

function segment(json: string): string {
	return base64Encode(json).replace(/=+$/, '').replaceAll('+', '-').replaceAll('/', '_')
}

describe('jwt (Krackn Tests/unit F)', () => {
	it('jwt: decodes payload', () => {
		expect(decodeJWT('eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjMifQ.sig', context)?.payload).toContain('123')
	})
	it('jwt: garbage -> nil', () => {
		expect(decodeJWT('notajwt', context)).toBeNull()
	})
})

// Zing additions.
describe('known answers', () => {
	it('pretty-prints header and payload and keeps the signature as is', () => {
		expect(decodeJWT('eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjMifQ.sig', context)).toEqual({
			header: '{\n  "alg": "HS256"\n}',
			payload: '{\n  "sub": "123"\n}',
			signature: 'sig',
			expiry: null,
		})
	})
	it('says whether exp has passed', () => {
		const past = decodeJWT(`${segment('{}')}.${segment('{"exp":1700000000}')}.x`, context)
		const future = decodeJWT(`${segment('{}')}.${segment('{"exp":1800000000}')}.x`, context)
		expect(past?.expiry).toMatch(/^Nov 14, 2023, 10:13:20\sPM {2}\(EXPIRED\)$/)
		expect(future?.expiry).toMatch(/ {2}\(valid\)$/)
	})
	it('a segment that is not base64 text is marked unreadable; one that is not JSON is shown raw', () => {
		const parts = decodeJWT(`!!!.${segment('not json')}`, context)
		expect(parts?.header).toBe('(unreadable)')
		expect(parts?.payload).toBe('not json')
		expect(parts?.signature).toBe('')
	})
})
