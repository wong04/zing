// pdf-lib runs here, off the interface's thread: loading, copying and saving a large file can take seconds. What stays on
// the main thread is the panel itself and the page pictures (pdf.js has a worker of its own for parsing).
// It reads untrusted PDFs and needs no network, so it is narrowed the same way as pdf.js's worker.
import './pdfjsGuard'
import { buildPdf, imagesToPdf, inspectPdf, PdfEditError } from '@shared/pdf/pdfEdit'

interface Scope {
	onmessage: ((event: MessageEvent<{ id: number; op: keyof typeof operations; args: never[] }>) => void) | null
	postMessage(message: unknown, transfer?: Transferable[]): void
}

const operations = { inspect: inspectPdf, build: buildPdf, imagesToPdf } as const

const scope = self as unknown as Scope

scope.onmessage = (event) => {
	const { id, op, args } = event.data
	const run = operations[op] as (...a: never[]) => Promise<unknown>
	run(...args).then(
		(result) => scope.postMessage({ id, ok: true, result }, result instanceof Uint8Array ? [result.buffer] : []),
		(error: unknown) => scope.postMessage({ id, ok: false, name: error instanceof PdfEditError ? 'PdfEditError' : 'Error', message: error instanceof Error ? error.message : String(error) }),
	)
}
