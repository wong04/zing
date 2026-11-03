import qrcode from 'qrcode-generator'
import { describe, expect, it } from 'vitest'
import { encodeQr, QrTooLongError } from './qr'

/** What the library makes when its own byte step is replaced by UTF-8, the route that needs no trick. */
function withLibraryUtf8(text: string): boolean[][] {
	const original = qrcode.stringToBytes
	qrcode.stringToBytes = (text: string) => Array.from(new TextEncoder().encode(text))
	try {
		const code = qrcode(0, 'M')
		code.addData(text)
		code.make()
		const size = code.getModuleCount()
		return Array.from({ length: size }, (_, row) => Array.from({ length: size }, (_, col) => code.isDark(row, col)))
	} finally {
		qrcode.stringToBytes = original
	}
}

describe('encodeQr', () => {
	it('encodes 日 differently from å, which shares its low byte', () => {
		expect(encodeQr('日')).not.toEqual(encodeQr('å'))
	})
	it.each(['日', '日本語 text', 'å', '😀', 'plain ascii'])('matches the modules for the UTF-8 bytes of %s', (text) => {
		expect(encodeQr(text)).toEqual(withLibraryUtf8(text))
	})
	it('raises a typed error for text no QR code can hold', () => {
		expect(() => encodeQr('x'.repeat(5000))).toThrow(QrTooLongError)
	})
})
