import { mkdtempSync, readdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, describe, expect, it } from 'vitest'
import { AccountError, AccountFileError, AccountStore, type SecretCipher } from './account'

const MARK = Buffer.from('SEALED:')

/** Stands in for DPAPI: reversible, but the bytes on disk are not the input. */
const cipher: SecretCipher = {
	isAvailable: () => true,
	encrypt: (plain) => Buffer.concat([MARK, Buffer.from(plain).reverse()]),
	decrypt: (sealed) => {
		if (!sealed.subarray(0, MARK.length).equals(MARK)) throw new Error('not sealed by this key')
		return Buffer.from(sealed.subarray(MARK.length)).reverse()
	},
}

let dirs: string[] = []
function tempDir(): string {
	const dir = mkdtempSync(join(tmpdir(), 'zing-account-'))
	dirs.push(dir)
	return dir
}
afterEach(() => {
	for (const dir of dirs) rmSync(dir, { recursive: true, force: true })
	dirs = []
})

function store(dir: string, overrides: Partial<ConstructorParameters<typeof AccountStore>[0]> = {}): AccountStore {
	return new AccountStore({ dir, cipher, ...overrides })
}

function codeOf(run: () => void): string | undefined {
	try {
		run()
	} catch (error) {
		return error instanceof AccountError ? error.code : String(error)
	}
	return undefined
}

describe('AccountStore', () => {
	it('starts with no account and signed out', () => {
		expect(store(tempDir()).state()).toMatchObject({ hasAccount: false, signedIn: false, profile: null })
	})

	it('signs up, then signs in again after a relaunch only with the right password', () => {
		const dir = tempDir()
		const first = store(dir)
		first.signUp('jdoe', 'Jane Doe', 'jane@example.com', 'correct horse')
		expect(first.state()).toMatchObject({ hasAccount: true, signedIn: true })

		const second = store(dir)
		expect(second.state().signedIn).toBe(true)
		second.signOut()
		expect(codeOf(() => second.signIn('wrong password'))).toBe('wrong-password')
		second.signIn('correct horse')
		expect(second.state().signedIn).toBe(true)
	})

	it('writes the password nowhere and seals its check', () => {
		const dir = tempDir()
		const password = 'a-very-distinctive-password-7731'
		store(dir).signUp('jdoe', 'Jane', '', password)
		for (const name of readdirSync(dir)) {
			const bytes = readFileSync(join(dir, name))
			expect(bytes.includes(Buffer.from(password))).toBe(false)
		}
		expect(readFileSync(join(dir, 'account.verifier')).subarray(0, MARK.length).equals(MARK)).toBe(true)
	})

	it('asks for the password at launch when told to', () => {
		const dir = tempDir()
		const first = store(dir)
		first.signUp('jdoe', 'Jane', '', 'longenough1')
		first.setRequirePasswordOnLaunch(true)
		expect(store(dir).state()).toMatchObject({ hasAccount: true, signedIn: false, requirePasswordOnLaunch: true })
	})

	it('rejects short passwords, blank fields and a second account', () => {
		const s = store(tempDir())
		expect(codeOf(() => s.signUp('jdoe', '', '', 'short'))).toBe('weak-password')
		expect(codeOf(() => s.signUp('  ', '', '', 'longenough1'))).toBe('empty-field')
		s.signUp('jdoe', '', '', 'longenough1')
		expect(codeOf(() => s.signUp('other', '', '', 'longenough1'))).toBe('account-exists')
	})

	it('refuses to sign up when the key store is unavailable instead of saving unprotected', () => {
		const s = store(tempDir(), { cipher: { ...cipher, isAvailable: () => false } })
		expect(codeOf(() => s.signUp('jdoe', '', '', 'longenough1'))).toBe('encryption-unavailable')
		expect(s.state().hasAccount).toBe(false)
	})

	it('needs a password reset when the sealed check cannot be opened', () => {
		const dir = tempDir()
		store(dir).signUp('jdoe', 'Jane', '', 'longenough1')
		const foreign: SecretCipher = {
			...cipher,
			decrypt: () => {
				throw new Error('different user')
			},
		}
		const moved = store(dir, { cipher: foreign })
		expect(moved.state()).toMatchObject({ hasAccount: true, needsPasswordReset: true, signedIn: false })
		const fresh = store(dir)
		expect(codeOf(() => fresh.resetPassword('anotherlong1'))).toBe('reset-not-needed')
	})

	it('updates the profile, and deleting removes every file', () => {
		const dir = tempDir()
		const s = store(dir)
		s.signUp('jdoe', 'Jane', '', 'longenough1')
		s.updateProfile('  Janet ', 'j@example.com')
		expect(s.state().profile).toEqual({ username: 'jdoe', displayName: 'Janet', email: 'j@example.com' })
		s.deleteAccount()
		expect(readdirSync(dir)).toEqual([])
		expect(s.state()).toMatchObject({ hasAccount: false, signedIn: false })
	})

	it('can start as a guest for development', () => {
		expect(store(tempDir(), { startAsGuest: true }).state().signedIn).toBe(true)
	})

	it('throws on a corrupt profile instead of pretending there is none', () => {
		const dir = tempDir()
		writeFileSync(join(dir, 'account.json'), '{ nope')
		expect(() => store(dir)).toThrow(AccountFileError)
	})
})
