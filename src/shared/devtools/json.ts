// Port of the JSON section of Krackn Features/Utilities/UtilityTools.swift.
//
// Fragments (a bare `123` or `"text"`) are allowed, as in Krackn.

/** Carries a human-readable parser message back to the UI. */
export class JSONProblem extends Error {
	override name = 'JSONProblem'
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
		if (error instanceof SyntaxError) throw new JSONProblem(error.message, { cause: error })
		throw error
	}
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
