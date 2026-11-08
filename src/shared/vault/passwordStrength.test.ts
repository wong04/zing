import { describe, expect, it } from 'vitest'
import { generatePassphrase, isStrongEnoughForSync, PASSPHRASE_LIST_SIZE, passphraseWords, SYNC_PASSWORD_SCORE } from './passwordStrength'
import vectors from './passwordStrength.vectors.json'

describe('the sync password floor, as the host holds it', () => {
	it('is the score the vectors give the host', () => {
		expect(SYNC_PASSWORD_SCORE).toBe(vectors.minimumScore)
	})

	it.each(vectors.cases)('$name', ({ password, accepted }) => {
		expect(isStrongEnoughForSync(password)).toBe(accepted)
	})

	it('the generated vectors are words of the passphrase list', () => {
		const list = new Set(passphraseWords())
		for (const { password } of vectors.cases.filter((c) => 'generated' in c)) for (const word of password.split('-')) expect(list.has(word)).toBe(true)
	})
})

describe('the passphrase generator', () => {
	it('draws from 4096 distinct words, so five make 60 bits', () => {
		expect(passphraseWords()).toHaveLength(PASSPHRASE_LIST_SIZE)
		expect(new Set(passphraseWords()).size).toBe(PASSPHRASE_LIST_SIZE)
	})

	it('makes five words that pass the floor, different each time', () => {
		const made = Array.from({ length: 20 }, generatePassphrase)
		for (const passphrase of made) {
			expect(passphrase.split('-')).toHaveLength(5)
			expect(isStrongEnoughForSync(passphrase)).toBe(true)
		}
		expect(new Set(made).size).toBe(made.length)
	})
})
