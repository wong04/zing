import { RelayError } from '../../shared/unblock/relayEngine'
import {
	ConnectionFailedError,
	PinnedClientError,
	TimedOutError,
} from '../../shared/unblock/pinnedHTTPClient'

/** The caller's AbortSignal fired. Never retried and never falls through to another strategy. */
export class CancelledError extends RelayError {
	override name = 'CancelledError'
	constructor() {
		super('The request was cancelled.')
	}
}

export function throwIfCancelled(signal: AbortSignal): void {
	if (signal.aborted) throw new CancelledError()
}

export interface Deadline {
	/** Fires on the caller's cancel or when the time runs out. */
	readonly signal: AbortSignal
	/** Turns whatever a socket or fetch threw into a CancelledError, TimedOutError or ConnectionFailedError. */
	toError(error: unknown): Error
}

export function startDeadline(caller: AbortSignal, timeoutMs: number): Deadline {
	const timer = AbortSignal.timeout(timeoutMs)
	return {
		signal: AbortSignal.any([caller, timer]),
		toError(error) {
			// Cancel wins over timeout when both fired: the user's intent is the news.
			if (caller.aborted) return new CancelledError()
			if (timer.aborted) return new TimedOutError()
			if (error instanceof RelayError || error instanceof PinnedClientError) return error
			if (error instanceof Error) return new ConnectionFailedError(describe(error))
			throw error
		},
	}
}

function describe(error: Error): string {
	const cause = error.cause
	if (cause instanceof Error) return cause.message
	return error.message
}

/**
 * Resolves with `promise`, or rejects with CancelledError the moment `signal`
 * fires. For operations (getaddrinfo) that cannot themselves be interrupted.
 */
export function abortable<T>(promise: Promise<T>, signal: AbortSignal): Promise<T> {
	if (signal.aborted) return Promise.reject(new CancelledError())
	return new Promise<T>((resolve, reject) => {
		const onAbort = (): void => reject(new CancelledError())
		signal.addEventListener('abort', onAbort, { once: true })
		promise.then(resolve, reject).finally(() => signal.removeEventListener('abort', onAbort))
	})
}
