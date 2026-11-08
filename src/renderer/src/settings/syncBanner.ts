import { offersUploadLocal, type SyncState } from '@shared/vault/vault'

/** What Upload this PC's copy does, said before it runs (Ruling 56). */
export const UPLOAD_LOCAL_CONFIRM = 'Zing merges this PC’s logins into the server’s copy. Other PCs that synced since the backup may ask the same.'

export interface SyncBanner {
	message: string
	// Upload this PC's copy answers it: the server went back to an older copy, or held a login back.
	offerUpload: boolean
}

/**
 * The one warning the Sync row shows: the last sync's error when there is one (the host pushes it in sync:state, an action that
 * failed with it included), otherwise what the last action here failed with. Never both, so one failure isn't said twice (M11).
 */
export function syncBanner(sync: SyncState, failure: string | undefined): SyncBanner | undefined {
	if (sync.signedIn && sync.error) return { message: sync.message ?? failure ?? 'Sync stopped.', offerUpload: offersUploadLocal(sync.error) }
	if (failure) return { message: failure, offerUpload: false }
	return undefined
}
