import qrcode from 'qrcode-generator'

export type QrModules = readonly (readonly boolean[])[]

const overflowMessage = 'code length overflow'

/** The text is longer than the largest QR code can hold. */
export class QrTooLongError extends Error {
	override name = 'QrTooLongError'
}

/**
 * The library turns each character into `charCode & 0xff`, which breaks everything past Latin-1. Handing it the
 * UTF-8 bytes, one per character, makes that same step produce the bytes every scanner expects.
 */
function utf8AsByteString(text: string): string {
	return Array.from(new TextEncoder().encode(text), (byte) => String.fromCharCode(byte)).join('')
}

export function encodeQr(text: string): QrModules {
	try {
		const code = qrcode(0, 'M')
		code.addData(utf8AsByteString(text))
		code.make()
		const size = code.getModuleCount()
		return Array.from({ length: size }, (_, row) => Array.from({ length: size }, (_, col) => code.isDark(row, col)))
	} catch (error) {
		// The library throws bare strings; this one means the text is too long for any QR version.
		if (typeof error === 'string' && error.startsWith(overflowMessage)) throw new QrTooLongError('That text is too long to fit in a QR code.')
		throw new Error(`QR encoding failed: ${String(error)}`, { cause: error })
	}
}
