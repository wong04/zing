import { offersUploadLocal, type SyncState } from '@shared/vault/vault'

/** What Upload this PC's copy does, said before it runs (Ruling 56). */
export const UPLOAD_LOCAL_CONFIRM = 'Zing merges this PC’s logins into the server’s copy. It can bring back logins deleted on another PC. Other PCs that synced since the backup may ask the same.'

export interface SyncBanner {
	message: string
	// Upload this PC's copy answers it: the server went back to an older copy, or held a login back.
	offerUpload: boolean
}

/**
 * The one warning the Sync row shows: what the last action here failed with, when it failed after the last state the host pushed
 * (the row drops it when a newer state comes, M3), otherwise the last sync's error. Never both, so one failure isn't said twice (M11).
 * Upload this PC's copy is offered whenever the last sync's error is one it answers.
 */
export function syncBanner(sync: SyncState, failure: string | undefined): SyncBanner | undefined {
	const offerUpload = sync.signedIn && offersUploadLocal(sync.error)
	if (failure) return { message: failure, offerUpload }
	if (sync.signedIn && sync.error) return { message: sync.message ?? 'Sync stopped.', offerUpload }
	return undefined
}
