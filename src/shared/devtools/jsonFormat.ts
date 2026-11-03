// One entry point for the JSON tool and its worker: format, or say where the text is wrong.
import { JSONProblem, JSONTooDeepError, minifyJSON, prettyJSON } from './json'

export type JSONMode = 'pretty' | 'minify'

export type JSONOutcome =
	| { readonly ok: true; readonly text: string }
	| { readonly ok: false; readonly message: string; readonly line: number | null }

/** Documents longer than this are formatted off the main thread (`json.worker.ts`). */
export const workerThreshold = 1_000_000

export function formatJSON(text: string, mode: JSONMode): JSONOutcome {
	if (text === '') return { ok: true, text: '' }
	try {
		return { ok: true, text: mode === 'pretty' ? prettyJSON(text) : minifyJSON(text) }
	} catch (error) {
		if (error instanceof JSONProblem) return { ok: false, message: error.message, line: error.line }
		if (error instanceof JSONTooDeepError) return { ok: false, message: error.message, line: null }
		throw error
	}
}

/**
 * What a result may weigh before the tool stops putting it in a text box. Measured in the app: setting a textarea
 * costs about 5 ms per 1,000 lines and about 0.1 ms per 1,000 characters on one line, so these two limits keep the
 * page blocked for roughly a quarter of a second at most.
 */
export const maxDisplayLines = 50_000
export const maxDisplayChars = 2_000_000

/** Stops counting at the limit: a result of millions of lines must not cost a second just to be refused. */
export function tooLargeToShow(text: string): boolean {
	if (text.length > maxDisplayChars) return true
	let lines = 1
	for (let at = text.indexOf('\n'); at !== -1; at = text.indexOf('\n', at + 1)) {
		lines++
		if (lines > maxDisplayLines) return true
	}
	return false
}
