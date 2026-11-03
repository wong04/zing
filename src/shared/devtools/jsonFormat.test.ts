import { describe, expect, it } from 'vitest'
import { formatJSON } from './jsonFormat'

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
})
