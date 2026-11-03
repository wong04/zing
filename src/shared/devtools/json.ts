// Port of the JSON section of Krackn Features/Utilities/UtilityTools.swift.
//
// Fragments (a bare `123` or `"text"`) are allowed, as in Krackn.

/** Carries a human-readable parser message back to the UI. */
export class JSONProblem extends Error {
	override name = 'JSONProblem'
	/** The line the parser stopped on, counting from 1. */
	readonly line: number

	constructor(message: string, line: number, options?: ErrorOptions) {
		super(message, options)
		this.line = line
	}
}

type JSONValue = null | boolean | number | string | JSONValue[] | { [key: string]: JSONValue }

const indentUnit = '  '

/** Throws `JSONProblem` when the text isn't JSON. */
export function validateJSON(text: string): void {
	parse(text)
}

/** Indented, with keys sorted so two documents can be compared by eye. */
export function prettyJSON(text: string): string {
	return writeSorted(parse(text), '')
}

/** One line, keys in their original order. */
export function minifyJSON(text: string): string {
	return JSON.stringify(parse(text))
}

function parse(text: string): JSONValue {
	try {
		return JSON.parse(text) as JSONValue
	} catch (error) {
		if (error instanceof SyntaxError) throw new JSONProblem(error.message, errorLine(error.message, text), { cause: error })
		throw error
	}
}

const reportedLine = /\(line (\d+) column \d+\)/
const reportedOffset = /position (\d+)/

/** Newer V8 messages name the position; "Unexpected token" ones don't, so those are located by scanning. */
function errorLine(message: string, text: string): number {
	const line = reportedLine.exec(message)?.[1]
	if (line !== undefined) return Number(line)
	const offset = reportedOffset.exec(message)?.[1]
	return linesBefore(text, offset === undefined ? firstBadOffset(text) : Number(offset)) + 1
}

function linesBefore(text: string, offset: number): number {
	return text.slice(0, offset).split('\n').length - 1
}

const number = /-?(?:0|[1-9]\d*)(?:\.\d+)?(?:[eE][+-]?\d+)?/y
// eslint-disable-next-line no-control-regex -- JSON forbids raw control characters in strings
const string = /"(?:[^"\\\u0000-\u001f]|\\(?:["\\/bfnrt]|u[0-9a-fA-F]{4}))*"/y
const whitespace = /[ \t\n\r]*/y
const literal = /true|false|null/y

/** Where the strict JSON grammar first stops accepting `text`: the offset of the bad character, or its length if it ends too soon. */
function firstBadOffset(text: string): number {
	const at = (pattern: RegExp, from: number): number => {
		pattern.lastIndex = from
		return pattern.test(text) ? pattern.lastIndex : -1
	}
	const skip = (from: number): number => at(whitespace, from)
	// Returns the offset just past the value, or -(offset of the bad character) - 1.
	const value = (from: number): number => {
		const start = skip(from)
		const char = text[start]
		if (char === '{') return members(start + 1)
		if (char === '[') return elements(start + 1)
		const end = char === '"' ? at(string, start) : at(char === 't' || char === 'f' || char === 'n' ? literal : number, start)
		return end < 0 ? -start - 1 : end
	}
	const members = (from: number): number => {
		let position = skip(from)
		if (text[position] === '}') return position + 1
		for (;;) {
			const keyEnd = at(string, position)
			if (keyEnd < 0) return -position - 1
			const colon = skip(keyEnd)
			if (text[colon] !== ':') return -colon - 1
			const end = value(colon + 1)
			if (end < 0) return end
			const next = skip(end)
			if (text[next] === '}') return next + 1
			if (text[next] !== ',') return -next - 1
			position = skip(next + 1)
		}
	}
	const elements = (from: number): number => {
		let position = skip(from)
		if (text[position] === ']') return position + 1
		for (;;) {
			const end = value(position)
			if (end < 0) return end
			const next = skip(end)
			if (text[next] === ']') return next + 1
			if (text[next] !== ',') return -next - 1
			position = skip(next + 1)
		}
	}
	const end = value(0)
	if (end < 0) return -end - 1
	return skip(end)
}

// Written out rather than `JSON.stringify(sorted, null, 2)`: a JS object always
// enumerates integer-like keys ("2", "10") first in numeric order, so a sorted
// copy can't be expressed as an object.
function writeSorted(value: JSONValue, indent: string): string {
	if (Array.isArray(value)) {
		if (value.length === 0) return '[]'
		const inner = indent + indentUnit
		return `[\n${value.map((item) => inner + writeSorted(item, inner)).join(',\n')}\n${indent}]`
	}
	if (value !== null && typeof value === 'object') {
		const entries = Object.entries(value).sort(([a], [b]) => (a < b ? -1 : a > b ? 1 : 0))
		if (entries.length === 0) return '{}'
		const inner = indent + indentUnit
		const members = entries.map(([key, member]) => `${inner}${JSON.stringify(key)}: ${writeSorted(member, inner)}`)
		return `{\n${members.join(',\n')}\n${indent}}`
	}
	return JSON.stringify(value)
}
