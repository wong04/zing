import { offersUploadLocal, type SyncNotice, type SyncState } from '@shared/vault/vault'

/** What Upload this PC's copy does, said before it runs (Ruling 56). */
export const UPLOAD_LOCAL_CONFIRM = 'Zing merges this PC’s logins into the server’s copy. It can bring back logins deleted on another PC. Other PCs that synced since the backup may ask the same.'

/** What Turn off sync on this PC does, said before it runs (Ruling 68). */
export const TURN_OFF_SYNC_CONFIRM = 'This PC stops syncing and keeps its logins. Your other PCs and the server copy aren’t changed.'

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

/** What sits in the Sync row: the account's buttons, Cancel while a sign-in waits for the browser, or the sign-in buttons. */
export type SyncControls = 'account' | 'cancel-sign-in' | 'sign-in'

/** Only a running sign-in shows Cancel; Turn off sync on this PC has its own busy state (Ruling 69). */
export function syncControls(sync: SyncState, signingIn: boolean): SyncControls {
	if (sync.signedIn) return 'account'
	if (signingIn) return 'cancel-sign-in'
	return 'sign-in'
}

function unsentText(count: number): string {
	return count === 1 ? '1 login hasn’t uploaded yet.' : `${count} logins haven’t uploaded yet.`
}

/** Sync pulls at start, after a change here and every 5 minutes, so the text never says another PC's edit shows up at once. */
export const SYNC_TIMING = 'Changes made on your other PCs come in when Zing starts, then every 5 minutes.'

export function syncDetail(sync: SyncState, signingIn: boolean): string {
	if (sync.signedIn && sync.on) return `Sync is on. Signed in as ${sync.email}. ${SYNC_TIMING}${sync.unsent > 0 ? ` ${unsentText(sync.unsent)}` : ''}`
	if (sync.signedIn) return `Signed in as ${sync.email}.`
	if (signingIn) return 'Finish signing in with Google in your browser.'
	// Sign out keeps the sync keys (Ruling 68), set up or still pending from a turn-on that never finished: signing in again resumes it.
	if (sync.keysHere) return 'This PC has sync keys but is signed out. Sign in again to continue.'
	return 'Keep saved passwords in step across your PCs. The server only ever sees them encrypted.'
}

/** The signed-out row's button: Sign in again while this PC is still set up for sync or its sign-in ended, otherwise Turn on sync. */
export function signInLabel(sync: SyncState, notice: SyncNotice | undefined): string {
	if (sync.keysHere || notice === 'signed-out' || notice === 'unreadable') return 'Sign in again'
	return 'Turn on sync'
}
