// Port of the case-conversion section of Krackn Features/Utilities/UtilityTools.swift.
import { graphemes, isUppercase, isWhitespace } from './graphemes'

export const CaseStyle = {
	upper: 'UPPER',
	lower: 'lower',
	title: 'Title Case',
	camel: 'camelCase',
	snake: 'snake_case',
	kebab: 'kebab-case',
} as const
export type CaseStyle = (typeof CaseStyle)[keyof typeof CaseStyle]

export const caseStyles: readonly CaseStyle[] = Object.values(CaseStyle)

export function toUpper(text: string): string {
	return text.toUpperCase()
}

export function toLower(text: string): string {
	return text.toLowerCase()
}

/** Foundation's `capitalized`: each whitespace-delimited word gets an upper-case first letter and a lower-case rest. */
export function toTitleCase(text: string): string {
	return text.replace(/[^\p{White_Space}]+/gu, capitalize)
}

export function toCamelCase(text: string): string {
	const [first, ...rest] = words(text)
	if (first === undefined) return ''
	return first.toLowerCase() + rest.map(capitalize).join('')
}

export function toSnakeCase(text: string): string {
	return words(text)
		.map((w) => w.toLowerCase())
		.join('_')
}

export function toKebabCase(text: string): string {
	return words(text)
		.map((w) => w.toLowerCase())
		.join('-')
}

const converters: Readonly<Record<CaseStyle, (text: string) => string>> = {
	[CaseStyle.upper]: toUpper,
	[CaseStyle.lower]: toLower,
	[CaseStyle.title]: toTitleCase,
	[CaseStyle.camel]: toCamelCase,
	[CaseStyle.snake]: toSnakeCase,
	[CaseStyle.kebab]: toKebabCase,
}

/** For the tool's style picker. */
export function convertCase(text: string, style: CaseStyle): string {
	return converters[style](text)
}

function capitalize(word: string): string {
	const [first = '', ...rest] = graphemes(word)
	return first.toUpperCase() + rest.join('').toLowerCase()
}

/**
 * Splits on whitespace, `_`, `-` and camelCase humps, so all three naming
 * conventions can round-trip into each other. (Krackn's comment said
 * "punctuation", but only `_` and `-` ever split; "a.b" stays one word.)
 */
function words(text: string): string[] {
	const result: string[] = []
	let current: string[] = []
	const flush = (): void => {
		if (current.length > 0) result.push(current.join(''))
		current = []
	}
	for (const character of graphemes(text)) {
		if (isWhitespace(character) || character === '_' || character === '-') {
			flush()
			continue
		}
		const last = current[current.length - 1]
		if (isUppercase(character) && last !== undefined && !isUppercase(last)) flush()
		current.push(character)
	}
	flush()
	return result
}
