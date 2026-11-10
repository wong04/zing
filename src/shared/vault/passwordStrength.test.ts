import { describe, expect, it } from 'vitest'
import {
	generatePassphrase,
	isStrongEnoughForSync,
	PASSPHRASE_LIST_SIZE,
	passphraseWords,
	SYNC_PASSWORD_MIN_LENGTH,
	SYNC_PASSWORD_RULES,
	SYNC_PASSWORD_SCORE,
	SYNC_PASSWORD_SENTENCES,
	syncPasswordFailures,
	syncStrength,
	syncStrengthLabel,
} from './passwordStrength'
import words from './passphraseWords.json'
import vectors from './passwordStrength.vectors.json'

describe('the sync password rules, as the host holds them', () => {
	it('are the ones the vectors give the host', () => {
		expect(SYNC_PASSWORD_MIN_LENGTH).toBe(vectors.minimumLength)
		expect(SYNC_PASSWORD_SCORE).toBe(vectors.minimumScore)
		expect(SYNC_PASSWORD_RULES).toEqual(vectors.rules)
		expect(SYNC_PASSWORD_SENTENCES).toEqual(vectors.sentences)
	})

	it.each(vectors.cases)('$name', ({ password, failed }) => {
		expect(syncPasswordFailures(password)).toEqual(failed)
		expect(isStrongEnoughForSync(password)).toBe(failed.length === 0)
	})

	it('the generated vectors are words of the passphrase list and a digit', () => {
		const list = new Set(passphraseWords())
		for (const { password } of vectors.cases.filter((c) => 'generated' in c)) {
			expect(password).toMatch(/\d$/)
			for (const word of password.slice(0, -1).split('-')) expect(list.has(word)).toBe(true)
		}
	})
})

describe('the passphrase generator', () => {
	it('draws from 4096 distinct words, so five make 60 bits', () => {
		expect(passphraseWords()).toHaveLength(PASSPHRASE_LIST_SIZE)
		expect(new Set(passphraseWords()).size).toBe(PASSPHRASE_LIST_SIZE)
	})

	it('draws from the committed list, so a zxcvbn update never changes the words', () => {
		expect(passphraseWords()).toEqual(words)
		for (const word of words) expect(word).toMatch(/^[a-z]{4,8}$/)
	})

	it('makes five words and a digit that pass the rules, different each time', () => {
		const made = Array.from({ length: 20 }, generatePassphrase)
		for (const passphrase of made) {
			expect(passphrase).toMatch(/^([a-z]{4,8}-){4}[a-z]{4,8}\d$/)
			expect(isStrongEnoughForSync(passphrase)).toBe(true)
		}
		expect(new Set(made).size).toBe(made.length)
	})
})

describe('the meter', () => {
	it('says the first failed rule as a sentence, and Strong enough only when every rule passes', () => {
		for (const { password, failed } of vectors.cases) {
			const first = failed[0] as keyof typeof vectors.sentences | undefined
			expect(syncStrengthLabel(syncStrength(password))).toBe(first ? vectors.sentences[first] : 'Strong enough for sync.')
		}
	})
})
