// What the two background-removal workers share. It must not import onnxruntime: the check worker, which runs before the
// model is even offered, would then carry it.
import { CorruptImageError, ImageTooLargeError, UnsupportedImageError } from '@shared/convert/imageSniff'
import type { FailureKind, RemoveMessage, RemoveStage } from './bgremoveProtocol'

/** A compressed picture of this size is already past anything a camera makes; the pixel limit is the real guard. */
export const MAX_FILE_BYTES = 120 * 1024 * 1024

export class WorkerFailure extends Error {
	override name = 'WorkerFailure'
	constructor(
		readonly kind: FailureKind,
		message: string,
	) {
		super(message)
	}
}

// The page's DOM typings are in use, and they give `self` the window's postMessage.
export const scope = self as unknown as { postMessage(message: RemoveMessage, transfer: Transferable[]): void; onmessage: ((event: MessageEvent<never>) => void) | null }

export function post(message: RemoveMessage, transfer: Transferable[] = []): void {
	scope.postMessage(message, transfer)
}

export const progress = (stage: RemoveStage, fraction?: number): void => post({ type: 'progress', stage, fraction })

/** Reads a whole response into one array, telling how far it has got. */
export async function download(url: string, what: string, limit: number, report: (fraction: number) => void): Promise<Uint8Array> {
	const response = await fetch(url)
	if (!response.ok) throw new WorkerFailure('unavailable', `${what} could not be read from Zing (${response.status}).`)
	const total = Number(response.headers.get('Content-Length'))
	if (!Number.isFinite(total) || total <= 0) throw new WorkerFailure('unavailable', `${what} came without a size, so Zing will not read it.`)
	if (total > limit) throw new WorkerFailure('too-large', `${what} is ${Math.round(total / 1048576)} MB, more than Zing will read.`)
	if (response.body === null) throw new WorkerFailure('unavailable', `${what} came empty.`)
	const bytes = new Uint8Array(total)
	let filled = 0
	const reader = response.body.getReader()
	for (;;) {
		const { done, value } = await reader.read()
		if (done) break
		if (filled + value.length > total) throw new WorkerFailure('unavailable', `${what} was longer than it said.`)
		bytes.set(value, filled)
		filled += value.length
		report(filled / total)
	}
	if (filled !== total) throw new WorkerFailure('unavailable', `${what} stopped early.`)
	return bytes
}

export function describe(error: unknown): RemoveMessage & { type: 'failed' } {
	if (error instanceof WorkerFailure) return { type: 'failed', kind: error.kind, message: error.message }
	if (error instanceof UnsupportedImageError) return { type: 'failed', kind: 'unsupported', message: error.message }
	if (error instanceof ImageTooLargeError) return { type: 'failed', kind: 'too-large', message: error.message }
	if (error instanceof CorruptImageError) return { type: 'failed', kind: 'corrupt', message: error.message }
	if (error instanceof RangeError) return { type: 'failed', kind: 'model', message: `The model answer was not usable: ${error.message}.` }
	return { type: 'failed', kind: 'unavailable', message: error instanceof Error ? error.message : String(error) }
}

