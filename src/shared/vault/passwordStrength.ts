// The sync password's rules, as the host enforces them (host/Zing.Core/Sync/SyncSetup.cs SyncPasswordStrength, zxcvbn-core): at
// least 12 characters, a digit, a character that's neither a letter nor a digit, and a zxcvbn score of at least 3. Characters are
// counted by code point, as the host counts runes. Both sides are held to the same failed rules by passwordStrength.vectors.json.
// The renderer's verdict only drives the meter and its sentence; the host's is the one that counts.
import { ZxcvbnFactory } from '@zxcvbn-ts/core'
import * as common from '@zxcvbn-ts/language-common'
import * as en from '@zxcvbn-ts/language-en'
import words from './passphraseWords.json'

export const SYNC_PASSWORD_MIN_LENGTH = 12

export const SYNC_PASSWORD_SCORE = 3

/** The rules in the order they're checked and reported. */
export const SYNC_PASSWORD_RULES = ['length', 'number', 'special', 'common'] as const

export type SyncPasswordRule = (typeof SYNC_PASSWORD_RULES)[number]

/** What the person is told about the first rule a password fails. */
export const SYNC_PASSWORD_SENTENCES: Readonly<Record<SyncPasswordRule, string>> = {
	length: 'Use at least 12 characters.',
	number: 'Add a number.',
	special: 'Add a symbol, such as ! or #.',
	common: 'This password is too common. Add more words or characters.',
}

export const PASSPHRASE_WORD_COUNT = 5

/** 2^12 words, so five drawn uniformly make 60 bits. */
export const PASSPHRASE_LIST_SIZE = 4096

let zxcvbn: ZxcvbnFactory | undefined

function factory(): ZxcvbnFactory {
	zxcvbn ??= new ZxcvbnFactory({
		dictionary: { ...common.dictionary, ...en.dictionary },
		graphs: common.adjacencyGraphs,
		translations: en.translations,
	})
	return zxcvbn
}

function failures(password: string, score: number): SyncPasswordRule[] {
	const characters = [...password]
	const failed: SyncPasswordRule[] = []
	if (characters.length < SYNC_PASSWORD_MIN_LENGTH) failed.push('length')
	if (!characters.some((c) => /\p{Nd}/u.test(c))) failed.push('number')
	if (!characters.some((c) => !/[\p{L}\p{Nd}]/u.test(c))) failed.push('special')
	if (score < SYNC_PASSWORD_SCORE) failed.push('common')
	return failed
}

/** zxcvbn's 0 to 4, the rules the password fails in their fixed order, and whether it passes them all. */
export function syncStrength(password: string): { score: number; failed: SyncPasswordRule[]; enough: boolean } {
	const score = factory().check(password).score
	const failed = failures(password, score)
	return { score, failed, enough: failed.length === 0 }
}

export const syncPasswordFailures = (password: string): SyncPasswordRule[] => syncStrength(password).failed

export const isStrongEnoughForSync = (password: string): boolean => syncStrength(password).enough

/** The meter's text: the first failed rule as a sentence, so the person knows why it would be turned down. */
export function syncStrengthLabel({ failed }: { failed: readonly SyncPasswordRule[] }): string {
	const first = failed[0]
	return first ? SYNC_PASSWORD_SENTENCES[first] : 'Strong enough for sync.'
}

/**
 * The passphrase words, frozen in passphraseWords.json so a zxcvbn update never changes them: zxcvbn-ts's English common words
 * (language-en 4.1.1), most common first, that are 4 to 8 plain letters, also in its Wikipedia list, not a first name, and not
 * one that reads badly in a password someone is asked to keep.
 */
export function passphraseWords(): readonly string[] {
	if (words.length !== PASSPHRASE_LIST_SIZE || new Set(words).size !== PASSPHRASE_LIST_SIZE) throw new Error(`passphraseWords.json must hold ${PASSPHRASE_LIST_SIZE} distinct words, not ${words.length}`)
	return words
}

/**
 * Five words drawn uniformly with the CSPRNG, joined by hyphens, and a random digit after the last: about 63 bits, with the
 * number and symbol the rules ask for. A draw that fails them (five of the most common words in a row, rarely) is drawn again,
 * so what's offered always passes the host's check.
 */
export function generatePassphrase(): string {
	const list = passphraseWords()
	for (;;) {
		const draw = new Uint32Array(PASSPHRASE_WORD_COUNT)
		crypto.getRandomValues(draw)
		// 2^32 is a multiple of 4096, so taking the low 12 bits has no modulo bias.
		const passphrase = Array.from(draw, (value) => list[value % PASSPHRASE_LIST_SIZE]).join('-') + randomDigit()
		if (isStrongEnoughForSync(passphrase)) return passphrase
	}
}

/** 0 to 9 with no modulo bias: bytes of 250 and up are drawn again. */
function randomDigit(): string {
	const byte = new Uint8Array(1)
	do crypto.getRandomValues(byte)
	while (byte[0]! >= 250)
	return String(byte[0]! % 10)
}
