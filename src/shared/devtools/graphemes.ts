// User-perceived characters. Krackn iterated Swift `Character`s, which are
// grapheme clusters, so "é" written as e + combining accent, or a flag emoji,
// counts as one character there and must here too.

const segmenter = new Intl.Segmenter(undefined, { granularity: 'grapheme' })

export function graphemes(text: string): string[] {
	return Array.from(segmenter.segment(text), (s) => s.segment)
}

/** Swift `Character.isWhitespace`: the Unicode White_Space property ("\r\n" is one whitespace character). */
export function isWhitespace(character: string): boolean {
	return /^\p{White_Space}+$/u.test(character)
}

/** Swift `Character.isUppercase`: changes under lowercasing, not under uppercasing. */
export function isUppercase(character: string): boolean {
	return character.toLowerCase() !== character && character.toUpperCase() === character
}
