import { PinnedClientError } from '../../shared/unblock/pinnedHTTPClient'
import {
	AllStrategiesFailedError,
	allRelayStrategies,
	normalize,
	RelayError,
	RelayStrategy,
} from '../../shared/unblock/relayEngine'
import { CancelledError, throwIfCancelled } from './abort'
import { fetchWith, type RelayResult } from './relayStrategies'

export interface RelayFetchOptions {
	readonly signal: AbortSignal
	/** Strategies to try; all four by default. */
	readonly enabled?: ReadonlySet<RelayStrategy>
	/** Tried first when it is enabled. */
	readonly preferred?: RelayStrategy
	readonly onProgress?: (message: string) => void
}

export interface RelayFetchResult extends RelayResult {
	/** What failed before this worked, for the "how did we get here" disclosure. */
	readonly attemptLog: readonly string[]
}

export function strategyOrder(enabled: ReadonlySet<RelayStrategy>, preferred?: RelayStrategy): RelayStrategy[] {
	const order = allRelayStrategies.filter((strategy) => enabled.has(strategy))
	if (preferred === undefined || !order.includes(preferred)) return order
	return [preferred, ...order.filter((strategy) => strategy !== preferred)]
}

/**
 * Tries each enabled strategy in Krackn's order and returns the first that
 * produces a body. A cancel stops everything: falling through to the next
 * strategy would make Cancel look like it did nothing.
 */
export async function relayFetch(rawInput: string, options: RelayFetchOptions): Promise<RelayFetchResult> {
	const { signal, onProgress } = options
	const url = normalize(rawInput)
	const order = strategyOrder(options.enabled ?? new Set(allRelayStrategies), options.preferred)
	const log: string[] = []

	for (const strategy of order) {
		throwIfCancelled(signal)
		onProgress?.(`Trying ${strategy}…`)
		try {
			const result = await fetchWith(strategy, url, signal)
			return { ...result, attemptLog: log }
		} catch (error) {
			if (error instanceof CancelledError) throw error
			if (!(error instanceof RelayError) && !(error instanceof PinnedClientError)) throw error
			const note = `✗ ${strategy}: ${error.message}`
			log.push(note)
			onProgress?.(note)
		}
	}
	throw new AllStrategiesFailedError(log)
}
