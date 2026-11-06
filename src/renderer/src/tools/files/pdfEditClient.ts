// The panel's side of the pdf-lib worker (pdfEdit.worker.ts): one worker, started on first use, ended when the panel closes
// or the person presses Cancel (which also drops the work in progress; nothing is kept).
import { PdfEditError, type PdfFile, type PdfInfo, type PdfPlan, type PictureFile } from '@shared/pdf/pdfCore'

interface Pending {
	readonly resolve: (value: unknown) => void
	readonly reject: (error: Error) => void
}

let worker: Worker | undefined
let nextId = 1
const pending = new Map<number, Pending>()

function start(): Worker {
	if (worker) return worker
	const created = new Worker(new URL('./pdfEdit.worker.ts', import.meta.url), { type: 'module' })
	created.onmessage = (event: MessageEvent<{ id: number; ok: boolean; result?: unknown; name?: string; message?: string }>) => {
		const answer = event.data
		const waiting = pending.get(answer.id)
		if (!waiting) return
		pending.delete(answer.id)
		if (answer.ok) waiting.resolve(answer.result)
		else waiting.reject(answer.name === 'PdfEditError' ? new PdfEditError(answer.message ?? '') : new Error(answer.message))
	}
	created.onerror = (event) => {
		const error = new Error(`The PDF worker stopped: ${event.message}`)
		for (const waiting of pending.values()) waiting.reject(error)
		pending.clear()
		created.terminate()
		if (worker === created) worker = undefined
	}
	worker = created
	return created
}

function call<T>(op: string, args: readonly unknown[]): Promise<T> {
	const id = nextId++
	return new Promise<T>((resolve, reject) => {
		pending.set(id, { resolve: resolve as (value: unknown) => void, reject })
		start().postMessage({ id, op, args })
	})
}

/** Ends the worker and fails whatever it was doing. */
export function stopPdfWork(): void {
	const error = new PdfEditError('Stopped. Nothing was kept.')
	for (const waiting of pending.values()) waiting.reject(error)
	pending.clear()
	worker?.terminate()
	worker = undefined
}

export const inspectInWorker = (file: PdfFile): Promise<PdfInfo> => call('inspect', [file])
export const buildInWorker = (files: readonly PdfFile[], plan: PdfPlan): Promise<Uint8Array> => call('build', [files, plan])
export const imagesToPdfInWorker = (pictures: readonly PictureFile[]): Promise<Uint8Array> => call('imagesToPdf', [pictures])
