// Port of the password checks in Krackn Tests/unit (section C).
import { afterEach, describe, expect, it, vi } from 'vitest'
import { generatePassword, InvalidPasswordLengthError, passwordAlphabet } from './password'

const everything = { symbols: true, digits: true, uppercase: true }

describe('passwords (Krackn Tests/unit C)', () => {
	it.each([8, 20, 64])('password length %i', (length) => {
		expect(generatePassword({ length, ...everything })).toHaveLength(length)
	})
	it('password respects flags', () => {
		const noSym = generatePassword({ length: 200, symbols: false, digits: false, uppercase: false })
		expect(noSym).toMatch(/^[a-z]+$/)
	})
	it('password excludes ambiguous', () => {
		expect(generatePassword({ length: 500, symbols: false, digits: true, uppercase: true })).not.toMatch(/[0O1lI]/)
	})
	it('passwords are random', () => {
		const unique = new Set(Array.from({ length: 200 }, () => generatePassword({ length: 16, ...everything })))
		expect(unique.size).toBe(200)
	})
})

// Zing additions.
describe('randomness source', () => {
	afterEach(() => {
		vi.restoreAllMocks()
	})
	it('draws from crypto.getRandomValues, never Math.random', () => {
		const csprng = vi.spyOn(crypto, 'getRandomValues')
		const weak = vi.spyOn(Math, 'random')
		generatePassword({ length: 12, ...everything })
		expect(csprng).toHaveBeenCalled()
		expect(weak).not.toHaveBeenCalled()
	})
	it('redraws values that would bias the result', () => {
		const alphabetLength = passwordAlphabet({ length: 1, symbols: false, digits: false, uppercase: false }).length
		const biased = 2 ** 32 - 1 // in the partial top block for a 25-letter alphabet
		const draws = [biased, 3]
		vi.spyOn(crypto, 'getRandomValues').mockImplementation(<T extends ArrayBufferView | null>(array: T): T => {
			if (array instanceof Uint32Array) array[0] = draws.shift() ?? 0
			return array
		})
		expect(alphabetLength).toBe(25)
		expect(generatePassword({ length: 1, symbols: false, digits: false, uppercase: false })).toBe('d')
	})
})

describe('alphabet', () => {
	it('adds each class only when asked', () => {
		expect(passwordAlphabet({ length: 1, ...everything })).toBe(
			'abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789!@#$%^&*()-_=+[]{}',
		)
	})
	it('a negative or fractional length is an error', () => {
		expect(() => generatePassword({ length: -1, ...everything })).toThrow(InvalidPasswordLengthError)
		expect(() => generatePassword({ length: 2.5, ...everything })).toThrow(InvalidPasswordLengthError)
	})
})
