// One entry point for the JSON tool and its worker: format, or say where the text is wrong.
import { JSONProblem, minifyJSON, prettyJSON } from './json'

export type JSONMode = 'pretty' | 'minify'

export type JSONOutcome =
	| { readonly ok: true; readonly text: string }
	| { readonly ok: false; readonly message: string; readonly line: number }

/** Documents longer than this are formatted off the main thread (`json.worker.ts`). */
export const workerThreshold = 1_000_000

export function formatJSON(text: string, mode: JSONMode): JSONOutcome {
	if (text === '') return { ok: true, text: '' }
	try {
		return { ok: true, text: mode === 'pretty' ? prettyJSON(text) : minifyJSON(text) }
	} catch (error) {
		if (error instanceof JSONProblem) return { ok: false, message: error.message, line: error.line }
		throw error
	}
}
