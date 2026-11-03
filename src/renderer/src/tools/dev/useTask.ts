import { useEffect, useState } from 'react'

export type TaskState<Result> =
	| { readonly kind: 'idle' }
	| { readonly kind: 'running' }
	| { readonly kind: 'done'; readonly value: Result }
	| { readonly kind: 'failed'; readonly error: unknown }

type Settled<Request, Result> = {
	readonly request: Request
	readonly outcome: { readonly kind: 'done'; readonly value: Result } | { readonly kind: 'failed'; readonly error: unknown }
}

/**
 * Runs `run` for each new `request` (null means nothing to do) and reports where it stands.
 * A request that is replaced before it finishes is aborted and its answer discarded, so a
 * slow answer never overwrites a newer one. Both arguments must be stable between renders:
 * `run` a module-level function, `request` memoised on the values it is made from.
 */
export function useTask<Request extends object, Result>(
	request: Request | null,
	run: (request: Request, signal: AbortSignal) => Promise<Result>,
): TaskState<Result> {
	const [settled, setSettled] = useState<Settled<Request, Result> | null>(null)

	useEffect(() => {
		if (request === null) return
		const abort = new AbortController()
		// `run` may throw before it has a promise (a worker that can't be created); that is a failure to show, not a crash.
		let pending: Promise<Result>
		try {
			pending = run(request, abort.signal)
		} catch (error) {
			pending = Promise.reject(error)
		}
		pending.then(
			(value) => {
				if (!abort.signal.aborted) setSettled({ request, outcome: { kind: 'done', value } })
			},
			(error: unknown) => {
				if (!abort.signal.aborted) setSettled({ request, outcome: { kind: 'failed', error } })
			},
		)
		return () => abort.abort()
	}, [request, run])

	if (request === null) return { kind: 'idle' }
	if (settled?.request !== request) return { kind: 'running' }
	return settled.outcome
}
