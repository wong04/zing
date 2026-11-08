import { describe, expect, it } from 'vitest'
import type { SyncState } from '@shared/vault/vault'
import { syncBanner, syncControls, syncDetail, TURN_OFF_SYNC_CONFIRM, UPLOAD_LOCAL_CONFIRM } from './syncBanner'

const on = (patch: Partial<Extract<SyncState, { signedIn: true }>> = {}): SyncState => ({ signedIn: true, email: 'ada@example.test', on: true, recoveryPending: false, unsent: 0, keysHere: true, ...patch })

describe('UPLOAD_LOCAL_CONFIRM', () => {
	it('warns that the upload can bring back logins deleted on another PC (Ruling 60)', () => {
		expect(UPLOAD_LOCAL_CONFIRM).toContain('It can bring back logins deleted on another PC.')
	})
})

describe('TURN_OFF_SYNC_CONFIRM', () => {
	it('says this PC keeps its logins and nothing else changes (Ruling 68)', () => {
		expect(TURN_OFF_SYNC_CONFIRM).toBe('This PC stops syncing and keeps its logins. Your other PCs and the server copy aren’t changed.')
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

	it('shows an action’s own failure over an older sync error (M3)', () => {
		const state = on({ error: 'unreachable', message: 'Zing couldn’t reach the sync server.' })
		expect(syncBanner(state, 'Zing couldn’t write sync.json')).toEqual({ message: 'Zing couldn’t write sync.json', offerUpload: false })
		expect(syncBanner(on({ error: 'server-withheld-item', message: 'Held back.' }), 'Sign in to sync first.')).toEqual({ message: 'Sign in to sync first.', offerUpload: true })
	})

	it('shows an action’s failure when the last sync has no error', () => {
		expect(syncBanner(on(), 'Signed out of sync, sign in again')).toEqual({ message: 'Signed out of sync, sign in again', offerUpload: false })
		expect(syncBanner({ signedIn: false, on: false, recoveryPending: false, keysHere: false }, 'Sync isn’t set up')).toMatchObject({ offerUpload: false })
	})

	it('shows the error of a turn-on still settling, before sync is on', () => {
		expect(syncBanner(on({ on: false, error: 'unreachable', message: 'Zing couldn’t reach the sync server.' }), undefined)).toMatchObject({ offerUpload: false })
	})

	it('shows nothing when all is well', () => {
		expect(syncBanner(on(), undefined)).toBeUndefined()
	})
})

describe('syncControls and syncDetail', () => {
	const signedOutWithKeys: SyncState = { signedIn: false, on: false, recoveryPending: false, keysHere: true }

	it('shows Cancel and Finish signing in only while a sign-in runs', () => {
		expect(syncControls(signedOutWithKeys, true)).toBe('cancel-sign-in')
		expect(syncDetail(signedOutWithKeys, true)).toBe('Finish signing in with Google in your browser.')
	})

	// Turn-off has its own busy state, so the row passes signingIn false while it runs (Ruling 69).
	it('keeps the sign-in buttons and the plain detail while Turn off sync on this PC runs', () => {
		expect(syncControls(signedOutWithKeys, false)).toBe('sign-in')
		expect(syncDetail(signedOutWithKeys, false)).not.toContain('Finish signing in')
	})

	it('shows the account controls when signed in, whatever runs', () => {
		expect(syncControls(on(), true)).toBe('account')
		expect(syncDetail(on({ unsent: 1 }), false)).toBe('Sync is on. Signed in as ada@example.test. 1 login hasn’t uploaded yet.')
	})
})
