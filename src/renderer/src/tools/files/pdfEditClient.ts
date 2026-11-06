// The pdf-lib worker (pdfEdit.worker.ts) as a panel uses it. Every panel (and every Convert tool) makes its own client, so
// closing or cancelling one ends only its own worker.
import { createPdfWorkerClient, type PdfWorkerClient, type WorkerLike } from '@shared/pdf/pdfWorkerClient'

export function newPdfWorkerClient(): PdfWorkerClient {
	return createPdfWorkerClient({ spawn: () => new Worker(new URL('./pdfEdit.worker.ts', import.meta.url), { type: 'module' }) as unknown as WorkerLike })
}
