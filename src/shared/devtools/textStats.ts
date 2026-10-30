// Port of `analyze` from Krackn Features/Utilities/DevTools.swift.
import { graphemes, isWhitespace } from './graphemes'

/** Counts that matter when writing or preparing training data. */
export type TextStats = {
	readonly characters: number
	readonly charactersNoSpaces: number
	readonly words: number
	readonly lines: number
	readonly sentences: number
	readonly paragraphs: number
	/** Rough LLM token estimate — about 4 characters per token in English. */
	readonly approxTokens: number
	readonly readingMinutes: number
}

const wordsPerMinute = 220
const charactersPerToken = 4
/** Foundation's `CharacterSet.newlines`: LF, VT, FF, CR, NEL, and the Unicode line and paragraph separators. */
const newline = /[\n\v\f\r\x85\p{Zl}\p{Zp}]/u

export function analyze(text: string): TextStats {
	const characters = graphemes(text)
	// Windows text is CRLF; treat it as one line break, or every line and
	// paragraph would be miscounted.
	const unixText = text.replace(/\r\n/g, '\n')
	const words = text.split(/\p{White_Space}+/u).filter((w) => w !== '').length
	return {
		characters: characters.length,
		charactersNoSpaces: characters.filter((c) => !isWhitespace(c)).length,
		words,
		lines: text === '' ? 0 : unixText.split(newline).length,
		sentences: nonBlankCount(text.split(/[.!?]/)),
		paragraphs: nonBlankCount(unixText.split('\n\n')),
		approxTokens: Math.round(characters.length / charactersPerToken),
		readingMinutes: words / wordsPerMinute,
	}
}

function nonBlankCount(parts: readonly string[]): number {
	return parts.filter((part) => part.trim() !== '').length
}
