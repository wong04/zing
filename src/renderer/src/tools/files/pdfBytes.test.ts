import { describe, expect, it } from 'vitest'
import { PdfEditError } from '@shared/pdf/pdfCore'
import { base64ToBytes, bytesToBase64, checkSaveSize } from './pdfBytes'

describe('base64', () => {
	it('round-trips every byte value, including more than one chunk', () => {
		const bytes = Uint8Array.from({ length: 100_000 }, (_, i) => i % 256)
		expect(base64ToBytes(bytesToBase64(bytes))).toEqual(bytes)
	})
	it('agrees with Buffer, and handles nothing', () => {
		const bytes = new Uint8Array([0, 255, 10, 13, 128])
		expect(bytesToBase64(bytes)).toBe(Buffer.from(bytes).toString('base64'))
		expect(base64ToBytes('')).toEqual(new Uint8Array())
	})
	it('refuses text that is not base64', () => {
		expect(() => base64ToBytes('not base64!!')).toThrow()
	})
})

describe('the save size', () => {
	it('refuses a file over the limit before it is turned into text', () => {
		expect(() => checkSaveSize({ length: 100 * 1024 * 1024 + 1 } as Uint8Array)).toThrow(PdfEditError)
		expect(() => checkSaveSize({ length: 100 * 1024 * 1024 } as Uint8Array)).not.toThrow()
	})
})
