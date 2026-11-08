import { describe, expect, it } from 'vitest'
import type { SyncState } from '@shared/vault/vault'
import { syncBanner, UPLOAD_LOCAL_CONFIRM } from './syncBanner'

const on = (patch: Partial<Extract<SyncState, { signedIn: true }>> = {}): SyncState => ({ signedIn: true, email: 'ada@example.test', on: true, recoveryPending: false, unsent: 0, ...patch })

describe('UPLOAD_LOCAL_CONFIRM', () => {
	it('warns that the upload can bring back logins deleted on another PC (Ruling 60)', () => {
		expect(UPLOAD_LOCAL_CONFIRM).toContain('It can bring back logins deleted on another PC.')
	})
})

describe('syncBanner', () => {
	it('offers Upload this PC’s copy after a rollback and after a held-back login', () => {
		expect(syncBanner(on({ error: 'server-rolled-back', message: 'The sync server went back.' }), undefined)).toEqual({ message: 'The sync server went back.', offerUpload: true })
		expect(syncBanner(on({ error: 'server-withheld-item', message: 'Held back.' }), undefined)).toMatchObject({ offerUpload: true })
	})

	it('shows other errors without the offer', () => {
		expect(syncBanner(on({ error: 'unreachable', message: 'Zing couldn’t reach the sync server.' }), undefined)).toEqual({ message: 'Zing couldn’t reach the sync server.', offerUpload: false })
	})

	it('says a failed upload once: the pushed error, not the rejection beside it', () => {
		const state = on({ error: 'server-rolled-back', message: 'The sync server went back.' })
		expect(syncBanner(state, 'The sync server went back.')).toEqual({ message: 'The sync server went back.', offerUpload: true })
	})

	it('shows an action’s failure when the last sync has no error', () => {
		expect(syncBanner(on(), 'Signed out of sync, sign in again')).toEqual({ message: 'Signed out of sync, sign in again', offerUpload: false })
		expect(syncBanner({ signedIn: false, on: false, recoveryPending: false }, 'Sync isn’t set up')).toMatchObject({ offerUpload: false })
	})

	it('shows the error of a turn-on still settling, before sync is on', () => {
		expect(syncBanner(on({ on: false, error: 'unreachable', message: 'Zing couldn’t reach the sync server.' }), undefined)).toMatchObject({ offerUpload: false })
	})

	it('shows nothing when all is well', () => {
		expect(syncBanner(on(), undefined)).toBeUndefined()
	})
})
