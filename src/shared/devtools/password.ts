// Port of `generatePassword` from Krackn Features/Utilities/UtilityTools.swift.

export type PasswordOptions = {
	readonly length: number
	readonly symbols: boolean
	readonly digits: boolean
	readonly uppercase: boolean
}

export class InvalidPasswordLengthError extends RangeError {
	override name = 'InvalidPasswordLengthError'
}

// Characters that are easy to misread are left out of every alphabet.
const lowercaseLetters = 'abcdefghijkmnopqrstuvwxyz' // no 'l'
const uppercaseLetters = 'ABCDEFGHJKLMNPQRSTUVWXYZ' // no 'I', 'O'
const digitCharacters = '23456789' // no '0', '1'
const symbolCharacters = '!@#$%^&*()-_=+[]{}'

const uint32Range = 2 ** 32

export function passwordAlphabet(options: PasswordOptions): string {
	let alphabet = lowercaseLetters
	if (options.uppercase) alphabet += uppercaseLetters
	if (options.digits) alphabet += digitCharacters
	if (options.symbols) alphabet += symbolCharacters
	return alphabet
}

/** CSPRNG-backed (`crypto.getRandomValues`); this is not the place for a seeded PRNG. */
export function generatePassword(options: PasswordOptions): string {
	if (!Number.isInteger(options.length) || options.length < 0) {
		throw new InvalidPasswordLengthError(`Password length must be a whole number ≥ 0, got ${options.length}`)
	}
	const alphabet = passwordAlphabet(options)
	return Array.from({ length: options.length }, () => alphabet[randomBelow(alphabet.length)]).join('')
}

/**
 * Uniform in 0..<bound. Values from the top, partial copy of the range are
 * redrawn, so no character is more likely than another (modulo bias).
 */
function randomBelow(bound: number): number {
	const limit = uint32Range - (uint32Range % bound)
	const draw = new Uint32Array(1)
	for (;;) {
		crypto.getRandomValues(draw)
		const value = draw[0] as number
		if (value < limit) return value % bound
	}
}
