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

/** Past this many matches the rows would freeze the page, so matching stops and says there are more. */
export const regexMatchLimit = 500

export type RegexResult = {
	/** The first `regexMatchLimit` at most. */
	readonly matches: readonly RegexMatch[]
	readonly more: boolean
}

const noMatches: RegexResult = { matches: [], more: false }

export function testRegex(pattern: string, subject: string): RegexResult {
	if (pattern === '') return noMatches
	return matchesOf(compile(pattern, 'g'), subject)
}

export function testRegexIgnoringCase(pattern: string, subject: string): RegexResult {
	if (pattern === '') return noMatches
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

function matchesOf(regex: RegExp, subject: string): RegexResult {
	const matches: RegexMatch[] = []
	for (const match of subject.matchAll(regex)) {
		if (matches.length === regexMatchLimit) return { matches, more: true }
		const start = match.index
		matches.push({
			text: match[0],
			range: `${start}–${start + match[0].length}`,
			groups: match.slice(1).map((group) => group ?? ''),
		})
	}
	return { matches, more: false }
}
