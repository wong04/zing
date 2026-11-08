// The Zing password's strength for sync, as the host enforces it (host/Zing.Core/Sync/SyncPasswordStrength.cs, zxcvbn-core):
// turning on sync needs zxcvbn score 4, about 60 bits or more. Both sides are held to the same verdict on clear cases by
// passwordStrength.vectors.json. The renderer's verdict only drives the meter; the host's is the one that counts.
import { ZxcvbnFactory } from '@zxcvbn-ts/core'
import * as common from '@zxcvbn-ts/language-common'
import * as en from '@zxcvbn-ts/language-en'

export const SYNC_PASSWORD_SCORE = 4

export const PASSPHRASE_WORD_COUNT = 5

/** 2^12 words, so five drawn uniformly make 60 bits. */
export const PASSPHRASE_LIST_SIZE = 4096

// Words of the list that would read badly in a password someone is asked to keep.
const LEFT_OUT = new Set([
	'nazi', 'nazis', 'negro', 'jews', 'jewish', 'hindus', 'massacre', 'killings', 'slaves', 'slavery', 'enslaved', 'bombing',
	'bombings', 'gunmen', 'gunfight', 'shootout', 'assault', 'assaults', 'death', 'deaths', 'died', 'bodies',
])

const dictionary = en.dictionary as Record<string, readonly string[]>

let zxcvbn: ZxcvbnFactory | undefined
let words: readonly string[] | undefined

function factory(): ZxcvbnFactory {
	zxcvbn ??= new ZxcvbnFactory({
		dictionary: { ...common.dictionary, ...en.dictionary },
		graphs: common.adjacencyGraphs,
		translations: en.translations,
	})
	return zxcvbn
}

/** zxcvbn's 0 to 4. */
export function passwordScore(password: string): number {
	return factory().check(password).score
}

export const isStrongEnoughForSync = (password: string): boolean => passwordScore(password) >= SYNC_PASSWORD_SCORE

/**
 * The passphrase words: zxcvbn's English common words, most common first, that are 4 to 8 plain letters, also in its Wikipedia
 * list, and not a first name. Zing has no word list of its own (the X21 password tool draws characters), and this one ships with
 * the strength meter, so the generator and the meter agree on what a word is.
 */
export function passphraseWords(): readonly string[] {
	if (words) return words
	const wikipedia = new Set(dictionary['wikipedia-en'])
	const firstNames = new Set(dictionary['firstnames-en'])
	const picked = (dictionary['commonWords-en'] ?? []).filter((word) => /^[a-z]{4,8}$/.test(word) && wikipedia.has(word) && !firstNames.has(word) && !LEFT_OUT.has(word))
	if (picked.length < PASSPHRASE_LIST_SIZE) throw new Error(`zxcvbn's English words give ${picked.length} passphrase words, fewer than ${PASSPHRASE_LIST_SIZE}`)
	words = picked.slice(0, PASSPHRASE_LIST_SIZE)
	return words
}

/**
 * Five words drawn uniformly with the CSPRNG, joined by hyphens: 60 bits. A draw zxcvbn scores below 4 (five of the most common
 * words in a row, rarely) is drawn again, so what's offered always passes the host's check.
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
