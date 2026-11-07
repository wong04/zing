// A pattern with nested quantifiers can run for longer than anyone will wait, and JavaScript
// cannot interrupt a running regex. Matching therefore happens here, so the tool can end it by
// terminating this thread (`runRegex` in workers.ts).
import { RegexProblem, testRegex, testRegexIgnoringCase, type RegexResult } from '@shared/devtools/regex'

export type RegexRequest = { readonly pattern: string; readonly subject: string; readonly ignoreCase: boolean }

export type RegexReply = ({ readonly ok: true } & RegexResult) | { readonly ok: false; readonly message: string }

function answer({ pattern, subject, ignoreCase }: RegexRequest): RegexReply {
	try {
		return { ok: true, ...(ignoreCase ? testRegexIgnoringCase(pattern, subject) : testRegex(pattern, subject)) }
	} catch (error) {
		if (error instanceof RegexProblem) return { ok: false, message: error.message }
		throw error
	}
}

self.onmessage = (event: MessageEvent<RegexRequest>) => {
	self.postMessage(answer(event.data))
}
