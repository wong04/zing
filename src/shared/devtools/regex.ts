// Port of `testRegex` from Krackn Features/Utilities/DevTools.swift.
//
// Krackn compiled patterns with NSRegularExpression (ICU syntax); Zing tests
// them as JavaScript regular expressions, which is what a pattern pasted from
// web code is written in.

/** Carries the parser's message back to the UI. */
export class RegexProblem extends Error {
	override name = 'RegexProblem'
}

export type RegexMatch = {
	readonly text: string
	/** UTF-16 offsets, "start–end". */
	readonly range: string
	/** One per capture group; an unmatched group is "". */
	readonly groups: readonly string[]
}

export function testRegex(pattern: string, subject: string): RegexMatch[] {
	if (pattern === '') return []
	return matchesOf(compile(pattern, 'g'), subject)
}

export function testRegexIgnoringCase(pattern: string, subject: string): RegexMatch[] {
	if (pattern === '') return []
	return matchesOf(compile(pattern, 'gi'), subject)
}

function compile(pattern: string, flags: string): RegExp {
	try {
		return new RegExp(pattern, flags)
	} catch (error) {
		if (error instanceof SyntaxError) throw new RegexProblem(error.message, { cause: error })
		throw error
	}
}

function matchesOf(regex: RegExp, subject: string): RegexMatch[] {
	return [...subject.matchAll(regex)].map((match) => {
		const start = match.index
		return {
			text: match[0],
			range: `${start}–${start + match[0].length}`,
			groups: match.slice(1).map((group) => group ?? ''),
		}
	})
}
