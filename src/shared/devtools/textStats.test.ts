// Port of the text-stats checks in Krackn Tests/unit (section F).
import { describe, expect, it } from 'vitest'
import { analyze } from './textStats'

describe('text stats (Krackn Tests/unit F)', () => {
	const st = analyze('Hello world. This is a test!\n\nSecond para.')
	it('stats: words', () => {
		expect(st.words).toBe(8)
	})
	it('stats: sentences', () => {
		expect(st.sentences).toBeGreaterThanOrEqual(2)
	})
	it('stats: paragraphs', () => {
		expect(st.paragraphs).toBe(2)
	})
	it('stats: tokens estimated', () => {
		expect(st.approxTokens).toBeGreaterThan(0)
	})
	it('stats: empty safe', () => {
		expect(analyze('').words).toBe(0)
	})
})

// Zing additions.
describe('known answers', () => {
	it('counts the sample exactly', () => {
		expect(analyze('Hello world. This is a test!\n\nSecond para.')).toEqual({
			characters: 42,
			charactersNoSpaces: 34,
			words: 8,
			lines: 3,
			sentences: 3,
			paragraphs: 2,
			approxTokens: 11,
			readingMinutes: 8 / 220,
		})
	})
	it('CRLF text counts the same as LF text', () => {
		const lf = analyze('one\ntwo\n\nthree')
		const crlf = analyze('one\r\ntwo\r\n\r\nthree')
		expect(crlf.lines).toBe(lf.lines)
		expect(crlf.paragraphs).toBe(lf.paragraphs)
		expect(crlf.words).toBe(lf.words)
	})
	it('Unicode line separators break lines; digits do not', () => {
		const lineSeparator = String.fromCodePoint(0x2028)
		expect(analyze(`a1${lineSeparator}b2028`).lines).toBe(2)
	})
	it('characters are what a person sees', () => {
		expect(analyze(`e${String.fromCodePoint(0x301)}🇯🇵`).characters).toBe(2)
	})
	it('empty text has no lines', () => {
		expect(analyze('')).toMatchObject({ characters: 0, lines: 0, sentences: 0, paragraphs: 0, approxTokens: 0 })
	})
})
