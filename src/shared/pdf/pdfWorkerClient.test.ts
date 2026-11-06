import { describe, expect, it } from 'vitest'
import { PdfEditError } from './pdfCore'
import { createPdfWorkerClient, type WorkerLike } from './pdfWorkerClient'

class FakeWorker implements WorkerLike {
	onmessage: ((event: { data: unknown }) => void) | null = null
	onerror: ((event: { message: string }) => void) | null = null
	posted: { id: number; op: string }[] = []
	terminated = false
	postMessage(message: unknown): void {
		this.posted.push(message as { id: number; op: string })
	}
	terminate(): void {
		this.terminated = true
	}
	answer(id: number, result: unknown): void {
		this.onmessage?.({ data: { id, ok: true, result } })
	}
}

function client(timeoutMs = 60_000) {
	const workers: FakeWorker[] = []
	const made = createPdfWorkerClient({ spawn: () => workers[workers.push(new FakeWorker()) - 1]!, timeoutMs })
	return { made, workers }
}

describe('each client has its own worker', () => {
	it('starts a worker on first use, not before', () => {
		const { workers } = client()
		expect(workers).toHaveLength(0)
	})
	it('answers a call from the worker', async () => {
		const { made, workers } = client()
		const call = made.inspect({ name: 'a.pdf', bytes: new Uint8Array() })
		workers[0]!.answer(workers[0]!.posted[0]!.id, { pageCount: 2, fields: [] })
		await expect(call).resolves.toEqual({ pageCount: 2, fields: [] })
	})
	it('stopping one client ends only its worker and fails only its own work', async () => {
		const a = client()
		const b = client()
		const callA = a.made.imagesToPdf([])
		const callB = b.made.imagesToPdf([])
		a.made.stop()
		await expect(callA).rejects.toThrow(PdfEditError)
		expect(a.workers[0]!.terminated).toBe(true)
		expect(b.workers[0]!.terminated).toBe(false)
		b.workers[0]!.answer(b.workers[0]!.posted[0]!.id, new Uint8Array([1]))
		await expect(callB).resolves.toEqual(new Uint8Array([1]))
	})
	it('starts a fresh worker for the next call after a stop', async () => {
		const { made, workers } = client()
		void made.imagesToPdf([]).catch(() => undefined)
		made.stop()
		void made.imagesToPdf([]).catch(() => undefined)
		expect(workers).toHaveLength(2)
		expect(workers[1]!.terminated).toBe(false)
	})
	it('ends a worker that takes too long, and says so', async () => {
		const { made, workers } = client(20)
		await expect(made.build([], { pages: [], overlays: [] })).rejects.toThrow(/took longer/)
		expect(workers[0]!.terminated).toBe(true)
	})
	it('turns a worker crash into an error for the waiting call', async () => {
		const { made, workers } = client()
		const call = made.imagesToPdf([])
		workers[0]!.onerror?.({ message: 'boom' })
		await expect(call).rejects.toThrow(/boom/)
		expect(workers[0]!.terminated).toBe(true)
	})
})
