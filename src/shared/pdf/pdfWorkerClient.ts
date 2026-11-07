// A panel's (or a Convert run's) own handle on a pdf-lib worker. One client, one worker, started on first use: stopping or
// timing out one client ends only its own worker, so closing one panel never kills another panel's job.
import { PdfEditError, type PdfFile, type PdfInfo, type PdfPlan, type PictureFile } from './pdfCore'

/** What the client needs of a Worker; a test supplies a fake. */
export interface WorkerLike {
	onmessage: ((event: { data: unknown }) => void) | null
	onerror: ((event: { message: string }) => void) | null
	onmessageerror: (() => void) | null
	postMessage(message: unknown): void
	terminate(): void
}

export interface PdfWorkerClient {
	inspect(file: PdfFile): Promise<PdfInfo>
	build(files: readonly PdfFile[], plan: PdfPlan): Promise<Uint8Array>
	imagesToPdf(pictures: readonly PictureFile[]): Promise<Uint8Array>
	/** Ends this client's worker and fails what it was doing. The next call starts a new worker. */
	stop(): void
}

export interface ClientOptions {
	readonly spawn: () => WorkerLike
	/** A call that takes longer ends the worker. */
	readonly timeoutMs?: number
}

interface Answer {
	id: number
	ok: boolean
	result?: unknown
	name?: string
	message?: string
}

interface Pending {
	readonly resolve: (value: unknown) => void
	readonly reject: (error: Error) => void
	readonly timer: ReturnType<typeof setTimeout>
}

export const DEFAULT_TIMEOUT_MS = 120_000

export function createPdfWorkerClient({ spawn, timeoutMs = DEFAULT_TIMEOUT_MS }: ClientOptions): PdfWorkerClient {
	let worker: WorkerLike | undefined
	let nextId = 1
	const pending = new Map<number, Pending>()

	function failAll(error: Error): void {
		const waiting = [...pending.values()]
		pending.clear()
		for (const w of waiting) {
			clearTimeout(w.timer)
			w.reject(error)
		}
	}

	function end(error: Error): void {
		const ended = worker
		worker = undefined
		ended?.terminate()
		failAll(error)
	}

	function start(): WorkerLike {
		if (worker) return worker
		const created = spawn()
		created.onmessage = (event) => {
			const answer = event.data as Answer
			const waiting = pending.get(answer.id)
			// A worker out of step with its calls can't be trusted with the rest of them.
			if (!waiting) return end(new Error(`The PDF worker answered call ${String(answer.id)}, which it was never sent`))
			pending.delete(answer.id)
			clearTimeout(waiting.timer)
			if (answer.ok) waiting.resolve(answer.result)
			else waiting.reject(answer.name === 'PdfEditError' ? new PdfEditError(answer.message ?? '') : new Error(answer.message))
		}
		created.onerror = (event) => {
			if (worker === created) end(new Error(`The PDF worker stopped: ${event.message}`))
		}
		created.onmessageerror = () => {
			if (worker === created) end(new Error('A reply from the PDF worker could not be read'))
		}
		worker = created
		return created
	}

	function call<T>(op: string, args: readonly unknown[]): Promise<T> {
		const id = nextId++
		return new Promise<T>((resolve, reject) => {
			// Started first: a spawn that throws rejects this call and arms no timer to end a later, healthy worker.
			const running = start()
			const timer = setTimeout(() => end(new PdfEditError(`The PDF worker took longer than ${Math.round(timeoutMs / 1000)} seconds, so it was stopped. The file may be too large or damaged.`)), timeoutMs)
			pending.set(id, { resolve: resolve as (value: unknown) => void, reject, timer })
			running.postMessage({ id, op, args })
		})
	}

	return {
		inspect: (file) => call('inspect', [file]),
		build: (files, plan) => call('build', [files, plan]),
		imagesToPdf: (pictures) => call('imagesToPdf', [pictures]),
		stop: () => end(new PdfEditError('Stopped. Nothing was kept.')),
	}
}
