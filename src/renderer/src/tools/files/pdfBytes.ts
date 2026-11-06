import { PDF_LIMITS, PdfEditError } from '@shared/pdf/pdfCore'

const CHUNK = 0x8000

export function bytesToBase64(bytes: Uint8Array): string {
	let binary = ''
	for (let at = 0; at < bytes.length; at += CHUNK) binary += String.fromCharCode(...bytes.subarray(at, at + CHUNK))
	return btoa(binary)
}

/** @throws DOMException (InvalidCharacterError) for text that is not base64. */
export function base64ToBytes(text: string): Uint8Array {
	const binary = atob(text)
	const bytes = new Uint8Array(binary.length)
	for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i)
	return bytes
}

export function checkSaveSize(bytes: Uint8Array): void {
	if (bytes.length > PDF_LIMITS.maxSaveBytes) {
		throw new PdfEditError(`The result is ${Math.ceil(bytes.length / (1024 * 1024))} MB; the PDF tools save files up to ${PDF_LIMITS.maxSaveBytes / (1024 * 1024)} MB.`)
	}
}
