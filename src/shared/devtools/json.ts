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

/** The document parses but is nested past what the writers here can recurse through. */
export class JSONTooDeepError extends Error {
	override name = 'JSONTooDeepError'
	constructor() {
		super(`Nested too deeply: this tool formats up to ${maxDepth} levels.`)
	}
}

const maxDepth = 1000

/** A number kept as the text it was typed as, so big integers and `1.0` survive (JSON.rawJSON, ES2025). */
interface RawNumber {
	readonly rawJSON: string
}

// JSON.parse source text access and JSON.rawJSON are in V8 (Chromium 114, Node 21) but not in the ES2023 lib.
const sourceJSON = JSON as JSON & {
	rawJSON(text: string): RawNumber
	isRawJSON(value: unknown): value is RawNumber
}

type JSONValue = null | boolean | number | RawNumber | string | JSONValue[] | { [key: string]: JSONValue }

const indentUnit = '  '

/** Throws `JSONProblem` when the text isn't JSON. */
export function validateJSON(text: string): void {
	parse(text)
}

/** Indented, with keys sorted so two documents can be compared by eye. */
export function prettyJSON(text: string): string {
	return writeSorted(parsedWithinDepth(text), '')
}

/** One line, keys in their original order. */
export function minifyJSON(text: string): string {
	return JSON.stringify(parsedWithinDepth(text))
}

function parsedWithinDepth(text: string): JSONValue {
	if (nestingOf(parse(text)) > maxDepth) throw new JSONTooDeepError()
	// Parsed again only once the depth is known to be safe: a reviver makes the engine recurse.
	return JSON.parse(text, keepNumberText) as JSONValue
}

/** Walks with its own stack: the document may be deeper than the call stack allows. */
function nestingOf(root: JSONValue): number {
	let deepest = 0
	const pending: [JSONValue, number][] = [[root, 1]]
	for (let next = pending.pop(); next !== undefined; next = pending.pop()) {
		const [value, depth] = next
		if (value === null || typeof value !== 'object' || sourceJSON.isRawJSON(value)) continue
		deepest = Math.max(deepest, depth)
		for (const member of Object.values(value)) pending.push([member, depth + 1])
	}
	return deepest
}

function parse(text: string): JSONValue {
	try {
		return JSON.parse(text) as JSONValue
	} catch (error) {
		if (error instanceof SyntaxError) throw new JSONProblem(error.message, errorLine(error.message, text), { cause: error })
		throw error
	}
}

function keepNumberText(_key: string, value: unknown, context?: { source: string }): unknown {
	if (typeof value !== 'number') return value
	if (context === undefined) throw new TypeError('JSON.parse gave the reviver no source text')
	return sourceJSON.rawJSON(context.source)
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

/**
 * Where the strict JSON grammar first stops accepting `text`: the offset of the bad character, or its
 * length if it ends too soon. Iterative with its own stack, so a deeply nested document can't overflow the call stack.
 */
function firstBadOffset(text: string): number {
	const at = (pattern: RegExp, from: number): number => {
		pattern.lastIndex = from
		return pattern.test(text) ? pattern.lastIndex : -1
	}
	const scalarEnd = (from: number): number => {
		const char = text[from]
		if (char === '"') return at(string, from)
		return at(char === 't' || char === 'f' || char === 'n' ? literal : number, from)
	}
	// What the next token must be: a value, a key, or (after a value) a comma or the container's close.
	type Expect = 'value' | 'valueOrClose' | 'keyOrClose' | 'key' | 'after'
	const open: string[] = []
	let expect: Expect = 'value'
	let position = 0
	for (;;) {
		position = at(whitespace, position)
		const char = text[position]
		if (expect === 'after') {
			const container = open.at(-1)
			if (container === undefined) return position
			if (char === ',') {
				expect = container === '{' ? 'key' : 'value'
			} else if (char === (container === '{' ? '}' : ']')) {
				open.pop()
			} else {
				return position
			}
			position++
			continue
		}
		if (expect === 'key' || expect === 'keyOrClose') {
			if (expect === 'keyOrClose' && char === '}') {
				open.pop()
				position++
				expect = 'after'
				continue
			}
			const keyEnd = at(string, position)
			if (keyEnd < 0) return position
			const colon = at(whitespace, keyEnd)
			if (text[colon] !== ':') return colon
			position = colon + 1
			expect = 'value'
			continue
		}
		if (expect === 'valueOrClose' && char === ']') {
			open.pop()
			position++
			expect = 'after'
			continue
		}
		if (char === '{' || char === '[') {
			open.push(char)
			position++
			expect = char === '{' ? 'keyOrClose' : 'valueOrClose'
			continue
		}
		const end = scalarEnd(position)
		if (end < 0) return position
		position = end
		expect = 'after'
	}
}

// Written out rather than `JSON.stringify(sorted, null, 2)`: a JS object always
// enumerates integer-like keys ("2", "10") first in numeric order, so a sorted
// copy can't be expressed as an object.
function writeSorted(value: JSONValue, indent: string): string {
	if (sourceJSON.isRawJSON(value)) return value.rawJSON
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
