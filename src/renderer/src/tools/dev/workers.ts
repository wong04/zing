import type { JSONOutcome } from '@shared/devtools/jsonFormat'
import { runInWorker } from './runInWorker'
import type { JSONRequest } from './json.worker'
import type { RegexReply, RegexRequest } from './regex.worker'

/** How long a pattern gets before the tester gives up on it. */
export const regexTimeoutMs = 2000

/** How long a big document gets before the JSON tool gives up. About 1 s of formatting for 10 MB on a desktop leaves 30 times that for a slow machine. */
export const jsonTimeoutMs = 30_000

// `new Worker(new URL(...))` stays inline: that exact shape is what the bundler recognises.
export function runRegex(request: RegexRequest, signal: AbortSignal): Promise<RegexReply> {
	const worker = new Worker(new URL('./regex.worker.ts', import.meta.url), { type: 'module' })
	return runInWorker<RegexRequest, RegexReply>(worker, request, signal, regexTimeoutMs)
}

export function runJSON(request: JSONRequest, signal: AbortSignal): Promise<JSONOutcome> {
	const worker = new Worker(new URL('./json.worker.ts', import.meta.url), { type: 'module' })
	return runInWorker<JSONRequest, JSONOutcome>(worker, request, signal, jsonTimeoutMs)
}
