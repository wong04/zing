// The Zing password's strength for sync, as the host enforces it (host/Zing.Core/Sync/SyncSetup.cs SyncPasswordStrength,
// zxcvbn-core): turning on sync needs zxcvbn score 4 and at least 10^18 guesses, about 60 bits (Ruling 52). Score 4 alone is
// only about 33 bits. Both sides are held to the same verdict on clear cases by passwordStrength.vectors.json. The renderer's
// verdict only drives the meter; the host's is the one that counts.
import { ZxcvbnFactory } from '@zxcvbn-ts/core'
import * as common from '@zxcvbn-ts/language-common'
import * as en from '@zxcvbn-ts/language-en'
import words from './passphraseWords.json'

export const SYNC_PASSWORD_SCORE = 4

/** 10^18 guesses is about 60 bits. */
export const SYNC_MIN_GUESSES_LOG10 = 18

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

/** zxcvbn's 0 to 4, and whether the password passes the sync floor. */
export function syncStrength(password: string): { score: number; enough: boolean } {
	const verdict = factory().check(password)
	return { score: verdict.score, enough: verdict.score >= SYNC_PASSWORD_SCORE && Math.log10(verdict.guesses) >= SYNC_MIN_GUESSES_LOG10 }
}

export const isStrongEnoughForSync = (password: string): boolean => syncStrength(password).enough

const SCORE_WORDS = ['Very weak', 'Weak', 'Fair', 'Good', 'Strong'] as const

/**
 * The meter's text. It never says Strong beside a refusal: zxcvbn's score 4 starts near 33 bits, so a score-4 password that
 * falls short (16 random characters, say) is told what sync needs instead (Ruling 54).
 */
export function syncStrengthLabel({ score, enough }: { score: number; enough: boolean }): string {
	if (enough) return 'Strong. Strong enough for sync.'
	if (score >= SYNC_PASSWORD_SCORE) return 'Not enough for sync yet: use five random words, or 18 or more random characters.'
	return `${SCORE_WORDS[score]}. Sync needs about 60 bits, such as five random words.`
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
 * Five words drawn uniformly with the CSPRNG, joined by hyphens: 60 bits. A draw below the floor (five of the most common words
 * in a row, rarely) is drawn again, so what's offered always passes the host's check.
 */
export function generatePassphrase(): string {
	const list = passphraseWords()
	for (;;) {
		const draw = new Uint32Array(PASSPHRASE_WORD_COUNT)
		crypto.getRandomValues(draw)
		// 2^32 is a multiple of 4096, so taking the low 12 bits has no modulo bias.
		const passphrase = Array.from(draw, (value) => list[value % PASSPHRASE_LIST_SIZE]).join('-')
		if (isStrongEnoughForSync(passphrase)) return passphrase
	}
}
