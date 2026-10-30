// Port of `UtilityTools.HashAlgorithm` (Krackn Features/Utilities/UtilityTools.swift).
// Kept apart from `hash.node.ts` so the renderer can list the choices without
// pulling in `node:crypto`.

export const HashAlgorithm = {
	md5: 'MD5',
	sha1: 'SHA-1',
	sha256: 'SHA-256',
	sha512: 'SHA-512',
} as const
export type HashAlgorithm = (typeof HashAlgorithm)[keyof typeof HashAlgorithm]

export const hashAlgorithms: readonly HashAlgorithm[] = Object.values(HashAlgorithm)

/**
 * MD5 and SHA-1 are broken for anything security-related, but they're still
 * what checksums on download pages use, so they stay available.
 */
export function isLegacy(algorithm: HashAlgorithm): boolean {
	return algorithm === HashAlgorithm.md5 || algorithm === HashAlgorithm.sha1
}
