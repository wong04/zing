import { describe, expect, it } from 'vitest'
import { bytesToBase64 } from './pdfBytes'

describe('base64 for data addresses', () => {
	it('agrees with btoa over more than one chunk', () => {
		const bytes = Uint8Array.from({ length: 100_000 }, (_, i) => i % 256)
		expect(bytesToBase64(bytes)).toBe(btoa(Array.from(bytes, (b) => String.fromCharCode(b)).join('')))
	})
	it('handles nothing', () => expect(bytesToBase64(new Uint8Array())).toBe(''))
})
