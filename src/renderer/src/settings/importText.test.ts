import { describe, expect, it } from 'vitest'
import { exportMenuPath, fileHelp, InvalidPasswordImportEventError, macBrowsers, outcomeLine, PASSWORDS_FILE_VAULT_OFF, passwordImportAccepts, passwordImportFailureApplies, passwordImportStep, type PasswordImportEvent, type PasswordImportState } from './importText'

const outcome = (patch: Partial<Parameters<typeof outcomeLine>[0]> = {}) => ({
	folderTitle: 'Imported from Chrome (Personal)',
	addedToBar: 0,
	addedToFolder: 0,
	skipped: [],
	...patch,
})

describe('outcomeLine', () => {
	it('says what went to the folder and the bar, and how many were skipped', () => {
		expect(outcomeLine(outcome({ addedToFolder: 412, addedToBar: 8, skipped: ['a', 'b', 'c'] }))).toBe(
			'Added 412 bookmarks to Imported from Chrome (Personal), 8 to the favourites bar; skipped 3.',
		)
	})

	it('uses the singular', () => {
		expect(outcomeLine(outcome({ addedToFolder: 1 }))).toBe('Added 1 bookmark to Imported from Chrome (Personal).')
		expect(outcomeLine(outcome({ addedToFolder: 2, addedToBar: 1 }))).toBe(
			'Added 2 bookmarks to Imported from Chrome (Personal), 1 to the favourites bar.',
		)
	})

	it('names only the bar when nothing went to the folder', () => {
		expect(outcomeLine(outcome({ addedToBar: 8 }))).toBe('Added 8 bookmarks to the favourites bar.')
		expect(outcomeLine(outcome({ addedToBar: 1 }))).toBe('Added 1 bookmark to the favourites bar.')
	})

	it('says nothing is new when nothing was added', () => {
		expect(outcomeLine(outcome())).toBe('Nothing new to import.')
		expect(outcomeLine(outcome({ skipped: ['x'] }))).toBe('Nothing new to import; skipped 1.')
	})
})

describe('fileHelp', () => {
	it('covers Firefox, which has no row of its own', () => {
		expect(fileHelp.map((h) => h.app)).toEqual(['Firefox'])
		for (const h of fileHelp) expect(h.steps).not.toBe('')
	})
})

describe('macBrowsers', () => {
	it('covers Dia, Safari and Arc with both exports', () => {
		expect(macBrowsers.map((m) => m.app)).toEqual(['Dia', 'Safari', 'Arc'])
		for (const m of macBrowsers) {
			expect(m.bookmarks).not.toBe('')
			expect(m.passwords).not.toBe('')
		}
	})
})

describe('exportMenuPath', () => {
	it('has a menu path for every browser with an export page, and none for Arc', () => {
		expect(exportMenuPath.chrome).toContain('Google Password Manager')
		expect(exportMenuPath.edge).toContain('Passwords')
		expect(exportMenuPath.brave).toContain('Password Manager')
		expect(exportMenuPath.arc).toBeUndefined()
	})
})

describe('PASSWORDS_FILE_VAULT_OFF', () => {
	it('says which setting to turn on', () => {
		expect(PASSWORDS_FILE_VAULT_OFF).toBe('Turn on “Offer to fill saved passwords” in Passwords to import a passwords file.')
	})
})

describe('passwordImportStep', () => {
	const run = (events: PasswordImportEvent[], from: PasswordImportState = { step: 'idle' }): PasswordImportState =>
		events.reduce((state, event) => passwordImportStep(state, event).state, from)

	it('goes idle, waiting, importing, imported, offer-recycle, done when the vault is on', () => {
		const steps: PasswordImportState['step'][] = []
		let state: PasswordImportState = { step: 'idle' }
		for (const event of [{ type: 'begin', vaultOn: true }, { type: 'file-arrived' }, { type: 'imported' }, { type: 'continue' }, { type: 'recycled' }] as const) {
			state = passwordImportStep(state, event).state
			steps.push(state.step)
		}
		expect(steps).toEqual(['waiting', 'importing', 'imported', 'offer-recycle', 'done'])
	})

	it('asks first when the vault is off', () => {
		expect(run([{ type: 'begin', vaultOn: false }])).toEqual({ step: 'confirm-vault' })
		expect(run([{ type: 'begin', vaultOn: false }, { type: 'confirm-vault' }])).toEqual({ step: 'waiting' })
		expect(run([{ type: 'begin', vaultOn: false }, { type: 'cancel' }])).toEqual({ step: 'idle' })
	})

	it('keeping the file also ends at done', () => {
		expect(run([{ type: 'kept' }], { step: 'offer-recycle' })).toEqual({ step: 'done' })
	})

	it('cancelling from waiting ends the watch; cancelling elsewhere has none to end', () => {
		expect(passwordImportStep({ step: 'waiting' }, { type: 'cancel' })).toEqual({ state: { step: 'idle' }, cancelWatch: true })
		expect(passwordImportStep({ step: 'confirm-vault' }, { type: 'cancel' }).cancelWatch).toBe(false)
		expect(passwordImportStep({ step: 'timed-out' }, { type: 'cancel' }).cancelWatch).toBe(false)
	})

	it('a timed-out watch offers the file picker, which enters at importing', () => {
		expect(run([{ type: 'timed-out' }], { step: 'waiting' })).toEqual({ step: 'timed-out' })
		expect(passwordImportStep({ step: 'timed-out' }, { type: 'choose-file' })).toEqual({ state: { step: 'importing' }, cancelWatch: false })
	})

	it('choosing the file by hand while waiting ends the watch', () => {
		expect(passwordImportStep({ step: 'waiting' }, { type: 'choose-file' })).toEqual({ state: { step: 'importing' }, cancelWatch: true })
	})

	it('a failure from any step is an error with the message; from waiting it ends the watch', () => {
		expect(passwordImportStep({ step: 'importing' }, { type: 'failed', message: 'nope' })).toEqual({ state: { step: 'error', message: 'nope' }, cancelWatch: false })
		expect(passwordImportStep({ step: 'waiting' }, { type: 'failed', message: 'Arc has no export' }).cancelWatch).toBe(true)
		expect(run([{ type: 'cancel' }], { step: 'error', message: 'x' })).toEqual({ step: 'idle' })
	})

	it('throws on an event the step cannot take', () => {
		expect(() => passwordImportStep({ step: 'idle' }, { type: 'file-arrived' })).toThrow(InvalidPasswordImportEventError)
		expect(() => passwordImportStep({ step: 'done' }, { type: 'recycled' })).toThrow(InvalidPasswordImportEventError)
	})
})

describe('late events', () => {
	it('an export arriving while the picker is open is dropped once the picked file is taken, and the reverse', () => {
		const importing = passwordImportStep({ step: 'waiting' }, { type: 'choose-file' }).state
		expect(passwordImportAccepts(importing, { type: 'file-arrived' })).toBe(false)
		expect(passwordImportAccepts(importing, { type: 'choose-file' })).toBe(false)
		expect(passwordImportAccepts({ step: 'waiting' }, { type: 'choose-file' })).toBe(true)
		expect(passwordImportAccepts({ step: 'timed-out' }, { type: 'choose-file' })).toBe(true)
		expect(passwordImportAccepts({ step: 'importing' }, { type: 'choose-file' })).toBe(false)
	})

	it('a second click on Move it to the Recycle Bin finds the step already done', () => {
		const done = passwordImportStep({ step: 'offer-recycle' }, { type: 'recycled' }).state
		expect(passwordImportAccepts(done, { type: 'recycled' })).toBe(false)
		expect(passwordImportAccepts({ step: 'offer-recycle' }, { type: 'recycled' })).toBe(true)
	})

	it('a failure applies only to the step that was waiting on it', () => {
		expect(passwordImportFailureApplies({ step: 'importing' }, 'importing')).toBe(true)
		expect(passwordImportFailureApplies({ step: 'idle' }, 'importing')).toBe(false)
	})
})
