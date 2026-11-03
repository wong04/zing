import { afterEach, describe, expect, it, vi } from 'vitest'
import { runInWorker, WorkerTimeoutError } from './runInWorker'
import { jsonTimeoutMs } from './workers'

class SilentWorker {
	terminated = false
	onmessage: ((event: MessageEvent) => void) | null = null
	onerror: ((event: ErrorEvent) => void) | null = null
	onmessageerror: (() => void) | null = null
	postMessage(): void {}
	terminate(): void {
		this.terminated = true
	}
}

afterEach(() => vi.useRealTimers())

describe('runInWorker', () => {
	it('stops a worker that never answers and rejects with the time it was given', async () => {
		vi.useFakeTimers()
		const worker = new SilentWorker()
		const answer = runInWorker(worker as unknown as Worker, {}, new AbortController().signal, jsonTimeoutMs)
		const settled = expect(answer).rejects.toThrow(WorkerTimeoutError)
		await vi.advanceTimersByTimeAsync(jsonTimeoutMs)
		await settled
		expect(worker.terminated).toBe(true)
	})
	it('gives a 10 MB document a generous 30 seconds', () => {
		// Formatting 10 MB took about one second on the development machine in Node; this leaves a slow one 30 times that.
		expect(jsonTimeoutMs).toBe(30_000)
	})
})
