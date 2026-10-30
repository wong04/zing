// Port of `hash`/`hashFile` (Krackn UtilityTools.swift) and `hmac`
// (Krackn DevTools.swift). Main/utility processes only: needs node:crypto,
// since Web Crypto has no MD5.
import { createHash, createHmac } from 'node:crypto'
import { createReadStream } from 'node:fs'
import { HashAlgorithm } from './hashAlgorithm'

const nodeAlgorithm: Readonly<Record<HashAlgorithm, string>> = {
	[HashAlgorithm.md5]: 'md5',
	[HashAlgorithm.sha1]: 'sha1',
	[HashAlgorithm.sha256]: 'sha256',
	[HashAlgorithm.sha512]: 'sha512',
}

/** Lowercase hex digest of the text's UTF-8 bytes. */
export function hash(text: string, algorithm: HashAlgorithm): string {
	return createHash(nodeAlgorithm[algorithm]).update(text, 'utf8').digest('hex')
}

/** Lowercase hex digest of a file, streamed so a large download doesn't have to fit in memory. */
export async function hashFile(path: string, algorithm: HashAlgorithm): Promise<string> {
	const digest = createHash(nodeAlgorithm[algorithm])
	for await (const chunk of createReadStream(path)) digest.update(chunk as Buffer)
	return digest.digest('hex')
}

/** HMAC over a message with a shared key — used constantly when signing API requests and webhooks. */
export function hmac(message: string, key: string, algorithm: HashAlgorithm): string {
	return createHmac(nodeAlgorithm[algorithm], Buffer.from(key, 'utf8')).update(message, 'utf8').digest('hex')
}
