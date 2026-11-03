import { RelayStrategy } from './relayEngine'
import type { RelayAttempt, RelayAttemptEvent, RelayOpenResult, RelayStrategyName } from './relayOpen'

/** What a tab opens for a run that reached the page. */
export type Opening =
	/** The direct method worked, so an ordinary tab at the address has no reason to fail. */
	| { readonly kind: 'ordinary'; readonly url: string }
	/** Any other method worked, so an ordinary tab would meet the same block. The relay scheme maps https only. */
	| { readonly kind: 'relayed'; readonly url: string }
	/** Another method worked for an http address, which can't be relayed as it is: the same address over https is tried once. */
	| { readonly kind: 'retryHttps'; readonly url: string }

const httpPrefix = 'http://'

export function httpsForm(address: string): string {
	return address.startsWith(httpPrefix) ? `https://${address.slice(httpPrefix.length)}` : address
}

export function planOpening(result: RelayOpenResult): Opening {
	if (result.strategy === 'direct') return { kind: 'ordinary', url: result.address }
	if (result.address.startsWith(httpPrefix)) return { kind: 'retryHttps', url: httpsForm(result.address) }
	return { kind: 'relayed', url: result.address }
}

export function strategyLabel(strategy: RelayStrategyName): string {
	return RelayStrategy[strategy]
}

/** The row of the attempt list after an attempt happens: the method's row changes, or a new one is added at the end. */
export function withAttempt(attempts: readonly RelayAttempt[], event: RelayAttemptEvent): readonly RelayAttempt[] {
	const next: RelayAttempt = { strategy: event.strategy, status: event.status, reason: event.reason }
	const index = attempts.findIndex((a) => a.strategy === event.strategy)
	if (index < 0) return [...attempts, next]
	return attempts.map((a, i) => (i === index ? next : a))
}

/** The result lists every step of a method (trying, then how it ended); the tool shows one row per method, as the pushes build it. */
export function collapseAttempts(steps: readonly RelayAttempt[]): readonly RelayAttempt[] {
	return steps.reduce<readonly RelayAttempt[]>((rows, step) => withAttempt(rows, { ...step, runId: '' }), [])
}

/** What the tool says about an attempt. A method failing is ordinary on a filtered network, and never reads as something broken. */
export function attemptSummary(attempt: RelayAttempt): string {
	switch (attempt.status) {
		case 'trying':
			return 'Trying…'
		case 'worked':
			return 'Worked'
		case 'skipped':
			return `Not tried. ${attempt.reason ?? ''}`.trim()
		case 'failed':
			return `Didn't get through. ${attempt.reason ?? ''}`.trim()
	}
}

/** Shown under a pinned attempt that failed: many networks filter encrypted DNS, so this is a normal outcome. */
export const pinnedFilteredNote = 'Many networks filter encrypted DNS, so this method not getting through is common and says nothing is wrong.'
