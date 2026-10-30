// Port of the JSON checks in Krackn Tests/unit (section C).
import { describe, expect, it } from 'vitest'
import { JSONProblem, minifyJSON, prettyJSON, validateJSON } from './json'

describe('json (Krackn Tests/unit C)', () => {
	it('json pretty', () => {
		expect(prettyJSON('{"a":1}')).toContain('\n')
	})
	it('json minify', () => {
		expect(minifyJSON('{"a": 1}')).not.toContain('\n')
	})
	it('json invalid rejected', () => {
		expect(() => prettyJSON('{bad}')).toThrow(JSONProblem)
	})
	it('json empty array', () => {
		expect(() => prettyJSON('[]')).not.toThrow()
	})
	it('json fragment', () => {
		expect(() => prettyJSON('123')).not.toThrow()
	})
	it('json empty rejected', () => {
		expect(() => prettyJSON('')).toThrow(JSONProblem)
	})
})

// Zing additions.
describe('known answers', () => {
	it('pretty output sorts keys at every level, numeric-looking ones included', () => {
		expect(prettyJSON('{"b":1,"10":[true,null],"2":{"z":"x","a":{}}}')).toBe(
			'{\n  "10": [\n    true,\n    null\n  ],\n  "2": {\n    "a": {},\n    "z": "x"\n  },\n  "b": 1\n}',
		)
	})
	it('minify keeps one line', () => {
		expect(minifyJSON('{ "a" : [ 1, 2 ],\n "b" : "c" }')).toBe('{"a":[1,2],"b":"c"}')
	})
	it('the problem carries the parser message', () => {
		expect(() => validateJSON('{"a":}')).toThrow(/\S/)
		expect(() => validateJSON('{"a":1}')).not.toThrow()
	})
	it('strings are escaped on the way out', () => {
		expect(prettyJSON('["a\\"b\\u00e9"]')).toBe('[\n  "a\\"bé"\n]')
	})
})
