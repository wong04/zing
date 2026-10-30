// Port of the case-conversion checks in Krackn Tests/unit (section C).
import { describe, expect, it } from 'vitest'
import { CaseStyle, convertCase, toCamelCase, toKebabCase, toSnakeCase, toTitleCase } from './textCase'

const acute = String.fromCodePoint(0x301) // combining acute accent

describe('text case (Krackn Tests/unit C)', () => {
	it('camel', () => {
		expect(convertCase('hello world again', CaseStyle.camel)).toBe('helloWorldAgain')
	})
	it('snake from camel', () => {
		expect(convertCase('helloWorldAgain', CaseStyle.snake)).toBe('hello_world_again')
	})
	it('kebab', () => {
		expect(convertCase('Hello World', CaseStyle.kebab)).toBe('hello-world')
	})
	it('case empty', () => {
		expect(convertCase('', CaseStyle.camel)).toBe('')
	})
	it('case unicode no crash', () => {
		expect(convertCase('café über', CaseStyle.snake)).not.toBe('')
	})
})

// Zing additions.
describe('known answers', () => {
	it('title case lowercases the rest of each word', () => {
		expect(toTitleCase('hELLO wORLD\tagain')).toBe('Hello World\tAgain')
	})
	it('acronyms stay one word until a lower-case hump', () => {
		expect(toSnakeCase('parseHTTPResponse')).toBe('parse_httpresponse')
		expect(toKebabCase('snake_case-and spaces')).toBe('snake-case-and-spaces')
	})
	it('camel case capitalises later words', () => {
		expect(toCamelCase('kebab-case_mixed Words')).toBe('kebabCaseMixedWords')
	})
	it('a combining accent stays with its letter', () => {
		expect(toSnakeCase(`E${acute}A`)).toBe(`e${acute}a`)
		expect(convertCase('café über', CaseStyle.snake)).toBe('café_über')
	})
	it('upper and lower', () => {
		expect(convertCase('MiXed', CaseStyle.upper)).toBe('MIXED')
		expect(convertCase('MiXed', CaseStyle.lower)).toBe('mixed')
	})
})
