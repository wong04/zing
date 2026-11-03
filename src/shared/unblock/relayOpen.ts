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

export interface RelayOpenResult {
	/** `needsConsent`: the site was not reached, and the next methods are third parties the user has not agreed to yet. */
	readonly outcome: 'opened' | 'needsConsent'
	readonly strategy?: RelayStrategyName
	readonly content: string
	readonly contentType?: string
	readonly isHtml: boolean
	readonly finalUrl?: string
	readonly attempts: readonly RelayAttempt[]
}
