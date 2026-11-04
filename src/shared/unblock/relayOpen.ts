// What Open a Link sends across the bridge (host/Zing.Core/Net/RelayEngine.cs).

export type RelayStrategyName = 'direct' | 'dohPinned' | 'readerRelay' | 'wayback'

export type AttemptStatus = 'trying' | 'failed' | 'worked' | 'skipped'

export interface RelayAttempt {
	readonly strategy: RelayStrategyName
	readonly status: AttemptStatus
	/** Why it failed or was skipped. */
	readonly reason?: string
}

/** Pushed as each attempt happens. `runId` is the one the caller gave `openLink`, so overlapping runs stay apart. */
export interface RelayAttemptEvent extends RelayAttempt {
	readonly runId: string
}

/** The page itself never crosses the bridge: it is someone else's text, and the tool only needs to know which way worked. */
export interface RelayOpenResult {
	/**
	 * `needsConsent`: the site was not reached, and the next methods are third parties the user has not agreed to yet.
	 * `warned`: the address looked unsafe, so nothing was fetched and a warning is open in a new tab.
	 */
	readonly outcome: 'opened' | 'needsConsent' | 'warned'
	readonly strategy?: RelayStrategyName
	/** What was typed, made into an address (`https://` added to a bare name). */
	readonly address: string
	readonly attempts: readonly RelayAttempt[]
}
