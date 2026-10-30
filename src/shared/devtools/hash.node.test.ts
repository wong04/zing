// Port of the hash/HMAC checks in Krackn Tests/unit (sections C and F).
import { createHash } from 'node:crypto'
import { mkdtempSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { describe, expect, it } from 'vitest'
import { hash, hashFile, hmac } from './hash.node'
import { HashAlgorithm, hashAlgorithms, isLegacy } from './hashAlgorithm'

describe('hashes (Krackn Tests/unit C)', () => {
	it('sha256 vector', () => {
		expect(hash('abc', HashAlgorithm.sha256)).toBe('ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad')
	})
	it('sha1 vector', () => {
		expect(hash('abc', HashAlgorithm.sha1)).toBe('a9993e364706816aba3e25717850c26c9cd0d89d')
	})
	it('md5 vector', () => {
		expect(hash('abc', HashAlgorithm.md5)).toBe('900150983cd24fb0d6963f7d28e17f72')
	})
	it('sha512 len', () => {
		expect(hash('abc', HashAlgorithm.sha512)).toHaveLength(128)
	})
	it('empty hash works', () => {
		expect(hash('', HashAlgorithm.sha256)).not.toBe('')
	})
	it('2MB hash', () => {
		expect(hash('x'.repeat(2_000_000), HashAlgorithm.sha256)).toHaveLength(64)
	})
	it('unicode hash', () => {
		expect(hash('🎉café日本', HashAlgorithm.sha256)).not.toBe('')
	})
})

describe('HMAC (Krackn Tests/unit F)', () => {
	it('hmac known vector', () => {
		expect(hmac('The quick brown fox jumps over the lazy dog', 'key', HashAlgorithm.sha256)).toBe(
			'f7bc83f430538424b13298e6aa6fb143ef4d59a14946175997479dbc2d1a3cd8',
		)
	})
	it('hmac differs by key', () => {
		expect(hmac('m', 'a', HashAlgorithm.sha256)).not.toBe(hmac('m', 'b', HashAlgorithm.sha256))
	})
})

// Zing additions: known answers for the vectors Krackn only checked by length.
describe('more known answers', () => {
	it('sha512 of "abc" (FIPS 180-2)', () => {
		expect(hash('abc', HashAlgorithm.sha512)).toBe(
			'ddaf35a193617abacc417349ae20413112e6fa4e89a97ea20a9eeee64b55d39a2192992a274fc1a836ba3c23a3feebbd454d4423643ce80e2a9ac94fa54ca49f',
		)
	})
	it('sha256 of "" ', () => {
		expect(hash('', HashAlgorithm.sha256)).toBe('e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855')
	})
	it('hashes UTF-8 bytes, not UTF-16 or Latin-1', () => {
		const utf8Bytes = Buffer.from([0xc3, 0xa9]) // "é"
		expect(hash('é', HashAlgorithm.md5)).toBe(createHash('md5').update(utf8Bytes).digest('hex'))
	})
	it('hmac md5 / sha1 / sha512 (Wikipedia vectors)', () => {
		const message = 'The quick brown fox jumps over the lazy dog'
		expect(hmac(message, 'key', HashAlgorithm.md5)).toBe('80070713463e7749b90c2dc24911e275')
		expect(hmac(message, 'key', HashAlgorithm.sha1)).toBe('de7c9b85b8b78aa6bc8a7a36f70a90701c9db4d9')
		expect(hmac(message, 'key', HashAlgorithm.sha512)).toBe(
			'b42af09057bac1e2d41708e48a902e09b5ff7f12ab428a4fe86653c73dd248fb82f948a549f7b791a5b41915ee4d1ec3935357e4e2317250d0372afa2ebeeb3a',
		)
	})
	it('MD5 and SHA-1 are flagged legacy, the SHA-2s are not', () => {
		expect(hashAlgorithms.filter(isLegacy)).toEqual(['MD5', 'SHA-1'])
	})
	it('a file hashes the same as its contents', async () => {
		const dir = mkdtempSync(join(tmpdir(), 'zing-hash-'))
		try {
			const path = join(dir, 'abc.txt')
			writeFileSync(path, 'abc')
			expect(await hashFile(path, HashAlgorithm.sha256)).toBe(hash('abc', HashAlgorithm.sha256))
		} finally {
			rmSync(dir, { recursive: true, force: true })
		}
	})
	it('a missing file is an error, not an empty digest', async () => {
		await expect(hashFile(join(tmpdir(), 'zing-definitely-missing.bin'), HashAlgorithm.md5)).rejects.toThrow()
	})
})
