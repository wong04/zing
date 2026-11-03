import { describe, expect, it } from 'vitest'
import { formatJSON, maxDisplayChars, maxDisplayLines, tooLargeToShow } from './jsonFormat'

describe('formatJSON', () => {
	it('formats {"a":1}', () => {
		expect(formatJSON('{"a":1}', 'pretty')).toEqual({ ok: true, text: '{\n  "a": 1\n}' })
	})
	it('minifies', () => {
		expect(formatJSON('{ "a": [1, 2] }', 'minify')).toEqual({ ok: true, text: '{"a":[1,2]}' })
	})
	it('empty input is not an error', () => {
		expect(formatJSON('', 'pretty')).toEqual({ ok: true, text: '' })
	})
	it('reports the line of a trailing comma', () => {
		expect(formatJSON('[\n1,\n]', 'pretty')).toMatchObject({ ok: false, line: 3 })
	})
	it('reports the line of an unquoted key', () => {
		expect(formatJSON('{\n"a": 1,\nb: 2\n}', 'pretty')).toMatchObject({ ok: false, line: 3 })
	})
	it('reports the line of a bad string escape', () => {
		expect(formatJSON('{\n"a": "x\\qy"\n}', 'pretty')).toMatchObject({ ok: false, line: 2 })
	})
	it('reports a missing value after a colon', () => {
		expect(formatJSON('{\n\t"a": 1,\n\t"b": }', 'pretty')).toMatchObject({ ok: false, line: 3 })
	})
	it('locates an error under 200,000 levels of nesting without overflowing the stack', () => {
		const text = '['.repeat(200_000) + '\n}'
		expect(formatJSON(text, 'pretty')).toMatchObject({ ok: false, line: 2 })
	})
	it('locates an error after a deep but valid prefix', () => {
		const text = '['.repeat(1000) + ']'.repeat(1000) + '\n]'
		expect(formatJSON(text, 'pretty')).toMatchObject({ ok: false, line: 2 })
	})
	it('says a valid document nested 20,000 deep is too deep, instead of throwing', () => {
		const text = '['.repeat(20_000) + ']'.repeat(20_000)
		expect(formatJSON(text, 'pretty')).toEqual({ ok: false, message: expect.stringContaining('Nested too deeply'), line: null })
		expect(formatJSON(text, 'minify')).toEqual({ ok: false, message: expect.stringContaining('Nested too deeply'), line: null })
	})
	it('still formats a document nested 500 deep', () => {
		const text = '['.repeat(500) + ']'.repeat(500)
		expect(formatJSON(text, 'minify')).toEqual({ ok: true, text })
		expect(formatJSON(text, 'pretty')).toMatchObject({ ok: true })
	})
})

describe('tooLargeToShow', () => {
	it('allows a result at the limits and refuses one past them', () => {
		expect(tooLargeToShow('\n'.repeat(maxDisplayLines - 1))).toBe(false)
		expect(tooLargeToShow('\n'.repeat(maxDisplayLines))).toBe(true)
		expect(tooLargeToShow('x'.repeat(maxDisplayChars))).toBe(false)
		expect(tooLargeToShow('x'.repeat(maxDisplayChars + 1))).toBe(true)
	})
})
