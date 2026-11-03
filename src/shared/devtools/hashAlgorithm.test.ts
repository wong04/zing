import { describe, expect, it } from 'vitest'
import { hashAlgorithms, isLegacy } from './hashAlgorithm'

describe('hash algorithms', () => {
	it('MD5 and SHA-1 are flagged legacy, the SHA-2s are not', () => {
		expect(hashAlgorithms.filter(isLegacy)).toEqual(['MD5', 'SHA-1'])
	})
})
