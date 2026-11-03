/** The worker did not answer in time, and was stopped. */
export class WorkerTimeoutError extends Error {
	override name = 'WorkerTimeoutError'
	readonly milliseconds: number

	constructor(milliseconds: number) {
		super(`No answer after ${milliseconds / 1000} seconds`)
		this.milliseconds = milliseconds
	}
}

/** The worker failed with something other than an answer. */
export class WorkerFailedError extends Error {
	override name = 'WorkerFailedError'
}

/**
 * Sends one request to a fresh worker and resolves with its single reply. The worker is
 * always stopped afterwards: on the reply, on `timeoutMs` (a pattern that never finishes
 * can only be ended by terminating its thread), or when `signal` aborts.
 */
export function runInWorker<Request, Reply>(worker: Worker, request: Request, signal: AbortSignal, timeoutMs?: number): Promise<Reply> {
	return new Promise<Reply>((resolve, reject) => {
		const timer = timeoutMs === undefined ? undefined : setTimeout(() => finish(() => reject(new WorkerTimeoutError(timeoutMs))), timeoutMs)
		const finish = (settle: () => void): void => {
			clearTimeout(timer)
			signal.removeEventListener('abort', onAbort)
			worker.terminate()
			settle()
		}
		const onAbort = (): void => finish(() => reject(signal.reason))
		if (signal.aborted) {
			onAbort()
			return
		}
		signal.addEventListener('abort', onAbort)
		worker.onmessage = (event: MessageEvent<Reply>) => finish(() => resolve(event.data))
		worker.onerror = (event) => finish(() => reject(new WorkerFailedError(event.message || 'The worker stopped unexpectedly')))
		worker.onmessageerror = () => finish(() => reject(new WorkerFailedError('The worker answered with something that could not be read')))
		worker.postMessage(request)
	})
}
