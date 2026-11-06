const CHUNK = 0x8000

/** For a data: address, the only way the interface's content security policy lets a picture be shown from bytes. */
export function bytesToBase64(bytes: Uint8Array): string {
	let binary = ''
	for (let at = 0; at < bytes.length; at += CHUNK) binary += String.fromCharCode(...bytes.subarray(at, at + CHUNK))
	return btoa(binary)
}
