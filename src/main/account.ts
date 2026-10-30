// Port of Krackn Features/Account/AccountStore.swift. A local account that
// gates the app on this computer: nothing is uploaded, there is no server.
//
// Krackn kept an HMAC of the password in the Keychain. Here the profile is a
// plain JSON file (nothing in it is a secret) and the password check is a
// scrypt hash that is additionally encrypted with the OS key store (Electron
// safeStorage, which is DPAPI on Windows) before it touches disk. The password
// itself is never written anywhere and never logged.
import { randomBytes, scryptSync, timingSafeEqual } from 'node:crypto'
import { mkdirSync, readFileSync, renameSync, rmSync, writeFileSync } from 'node:fs'
import { join } from 'node:path'
import {
	MIN_PASSWORD_LENGTH,
	type AccountErrorCode,
	type AccountProfile,
	type AccountState,
} from '@shared/shell/shellState'

export class AccountError extends Error {
	constructor(
		readonly code: AccountErrorCode,
		message: string,
	) {
		super(message)
		this.name = 'AccountError'
	}
}

const MESSAGES: Readonly<Record<AccountErrorCode, string>> = {
	'account-exists': 'An account already exists on this computer.',
	'weak-password': `Use at least ${MIN_PASSWORD_LENGTH} characters.`,
	'empty-field': 'Fill in every field.',
	'wrong-password': "That password doesn't match.",
	'no-account': 'No account yet. Create one first.',
	'encryption-unavailable': "This computer can't protect a saved password (the Windows key store is unavailable).",
	'reset-not-needed': 'Your password is already set.',
}

function fail(code: AccountErrorCode): never {
	throw new AccountError(code, MESSAGES[code])
}

export class AccountFileError extends Error {
	constructor(
		readonly path: string,
		options: { cause: unknown },
	) {
		super(`Could not read the account at ${path}: ${(options.cause as Error).message}`, options)
		this.name = 'AccountFileError'
	}
}

/** The OS key store, injected so the rules here can be tested without Electron. */
export interface SecretCipher {
	readonly isAvailable: () => boolean
	readonly encrypt: (plain: Buffer) => Buffer
	readonly decrypt: (sealed: Buffer) => Buffer
}

export interface AccountStoreOptions {
	readonly dir: string
	readonly cipher: SecretCipher
	/** Starts signed in as a guest. Only for development and tests; see index.ts. */
	readonly startAsGuest?: boolean
}

interface StoredAccount extends AccountProfile {
	readonly createdAt: string
	readonly salt: string
	readonly requirePassword: boolean
}

const PROFILE_FILE = 'account.json'
const VERIFIER_FILE = 'account.verifier'
const SALT_BYTES = 32
const KEY_BYTES = 32

function isMissing(error: unknown): boolean {
	return (error as NodeJS.ErrnoException).code === 'ENOENT'
}

export type AccountListener = (state: AccountState) => void

export class AccountStore {
	readonly #dir: string
	readonly #cipher: SecretCipher
	readonly #listeners = new Set<AccountListener>()
	#account: StoredAccount | null
	#signedIn: boolean

	constructor(options: AccountStoreOptions) {
		this.#dir = options.dir
		this.#cipher = options.cipher
		this.#account = this.#load()
		// A returning user is signed in automatically unless they asked for a
		// lock: "remember me" is the default, which is the whole point.
		const unlocked = this.#account !== null && !this.#account.requirePassword && !this.#needsReset()
		this.#signedIn = options.startAsGuest === true || unlocked
	}

	state(): AccountState {
		const account = this.#account
		return {
			hasAccount: account !== null,
			signedIn: this.#signedIn,
			needsPasswordReset: this.#needsReset(),
			requirePasswordOnLaunch: account?.requirePassword ?? false,
			profile: account ? { username: account.username, displayName: account.displayName, email: account.email } : null,
		}
	}

	subscribe(listener: AccountListener): () => void {
		this.#listeners.add(listener)
		return () => this.#listeners.delete(listener)
	}

	signUp(username: string, displayName: string, email: string, password: string): void {
		if (this.#account) fail('account-exists')
		const user = username.trim()
		if (user === '' || password === '') fail('empty-field')
		if (password.length < MIN_PASSWORD_LENGTH) fail('weak-password')
		if (!this.#cipher.isAvailable()) fail('encryption-unavailable')

		const salt = randomBytes(SALT_BYTES)
		this.#storeVerifier(password, salt)
		this.#save({
			username: user,
			displayName: displayName.trim() || user,
			email: email.trim(),
			createdAt: new Date().toISOString(),
			salt: salt.toString('hex'),
			requirePassword: false,
		})
		this.#signedIn = true
		this.#emit()
	}

	signIn(password: string): void {
		const account = this.#account ?? fail('no-account')
		const stored = this.#readVerifier() ?? fail('no-account')
		const candidate = this.#verifier(password, Buffer.from(account.salt, 'hex'))
		if (candidate.length !== stored.length || !timingSafeEqual(candidate, stored)) fail('wrong-password')
		this.#signedIn = true
		this.#emit()
	}

	/** For when the stored check can't be read; sets a new password on the same profile. */
	resetPassword(password: string): void {
		const account = this.#account ?? fail('no-account')
		if (!this.#needsReset()) fail('reset-not-needed')
		if (password.length < MIN_PASSWORD_LENGTH) fail('weak-password')
		if (!this.#cipher.isAvailable()) fail('encryption-unavailable')
		this.#storeVerifier(password, Buffer.from(account.salt, 'hex'))
		this.#signedIn = true
		this.#emit()
	}

	signOut(): void {
		this.#signedIn = false
		this.#emit()
	}

	continueAsGuest(): void {
		this.#signedIn = true
		this.#emit()
	}

	updateProfile(displayName: string, email: string): void {
		const account = this.#account ?? fail('no-account')
		this.#save({ ...account, displayName: displayName.trim() || account.username, email: email.trim() })
		this.#emit()
	}

	setRequirePasswordOnLaunch(required: boolean): void {
		const account = this.#account ?? fail('no-account')
		this.#save({ ...account, requirePassword: required })
		this.#emit()
	}

	deleteAccount(): void {
		rmSync(join(this.#dir, PROFILE_FILE), { force: true })
		rmSync(join(this.#dir, VERIFIER_FILE), { force: true })
		this.#account = null
		this.#signedIn = false
		this.#emit()
	}

	#verifier(password: string, salt: Buffer): Buffer {
		return scryptSync(password, salt, KEY_BYTES)
	}

	#storeVerifier(password: string, salt: Buffer): void {
		mkdirSync(this.#dir, { recursive: true })
		this.#writeAtomic(VERIFIER_FILE, this.#cipher.encrypt(this.#verifier(password, salt)))
	}

	#readVerifier(): Buffer | null {
		let sealed: Buffer
		try {
			sealed = readFileSync(join(this.#dir, VERIFIER_FILE))
		} catch (error) {
			if (isMissing(error)) return null
			throw new AccountFileError(join(this.#dir, VERIFIER_FILE), { cause: error })
		}
		try {
			return this.#cipher.decrypt(sealed)
		} catch {
			// A different Windows user or machine can't open it; the reset flow handles that.
			return null
		}
	}

	#needsReset(): boolean {
		return this.#account !== null && this.#readVerifier() === null
	}

	#load(): StoredAccount | null {
		const path = join(this.#dir, PROFILE_FILE)
		let raw: string
		try {
			raw = readFileSync(path, 'utf8')
		} catch (error) {
			if (isMissing(error)) return null
			throw new AccountFileError(path, { cause: error })
		}
		try {
			return parseStored(JSON.parse(raw))
		} catch (error) {
			throw new AccountFileError(path, { cause: error })
		}
	}

	#save(account: StoredAccount): void {
		mkdirSync(this.#dir, { recursive: true })
		this.#writeAtomic(PROFILE_FILE, Buffer.from(`${JSON.stringify(account, null, '\t')}\n`, 'utf8'))
		this.#account = account
	}

	#writeAtomic(name: string, data: Buffer): void {
		const path = join(this.#dir, name)
		const temp = `${path}.${process.pid}.tmp`
		writeFileSync(temp, data)
		renameSync(temp, path)
	}

	#emit(): void {
		const state = this.state()
		for (const listener of this.#listeners) listener(state)
	}
}

function parseStored(value: unknown): StoredAccount {
	const v = value as Partial<Record<keyof StoredAccount, unknown>> | null
	const strings = ['username', 'displayName', 'email', 'createdAt', 'salt'] as const
	if (typeof v !== 'object' || v === null) throw new TypeError('not an object')
	for (const key of strings) {
		if (typeof v[key] !== 'string') throw new TypeError(`${key} must be a string`)
	}
	if (typeof v.requirePassword !== 'boolean') throw new TypeError('requirePassword must be a boolean')
	return v as unknown as StoredAccount
}
