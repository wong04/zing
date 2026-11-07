import { afterEach, describe, expect, it } from 'vitest'
import { DEFAULT_SETTINGS, type Settings } from '@shared/settings'
import { DuplicateConditionError, privacyStatus, registerStoresDataCondition, setVaultKeepsData } from './privacy'

const withSettings = (patch: Partial<Settings>): Settings => ({ ...DEFAULT_SETTINGS, ...patch })

describe('privacyStatus', () => {
	it('is green with the defaults: Zing keeps nothing', () => {
		expect(privacyStatus(DEFAULT_SETTINGS)).toMatchObject({ traceless: true })
	})

	it('flips to a warning for restoreTabs and names what is kept', () => {
		const status = privacyStatus(withSettings({ restoreTabs: true }))
		expect(status.traceless).toBe(false)
		expect(status.detail).toBe('Your open tabs are written to disk.')
	})

	it('flips for a persistent profile', () => {
		const status = privacyStatus(withSettings({ persistentProfile: true }))
		expect(status).toMatchObject({ traceless: false, detail: 'Cookies and cache are written to disk.' })
	})

	it('lists everything kept when both are on', () => {
		const status = privacyStatus(withSettings({ restoreTabs: true, persistentProfile: true }))
		expect(status.detail).toBe('Cookies and cache and your open tabs are written to disk.')
	})
})

describe('registerStoresDataCondition', () => {
	it('lets another feature flip the banner, and put it back', () => {
		let vaultHasEntries = false
		const unregister = registerStoresDataCondition({
			id: 'test-vault',
			active: () => vaultHasEntries,
			what: 'saved passwords',
			plural: true,
		})
		expect(privacyStatus(DEFAULT_SETTINGS).traceless).toBe(true)
		vaultHasEntries = true
		expect(privacyStatus(DEFAULT_SETTINGS)).toMatchObject({ traceless: false, detail: 'Saved passwords are written to disk.' })
		unregister()
		expect(privacyStatus(DEFAULT_SETTINGS).traceless).toBe(true)
	})

	it('refuses two conditions with one id', () => {
		const unregister = registerStoresDataCondition({ id: 'test-dup', active: () => false, what: 'x' })
		expect(() => registerStoresDataCondition({ id: 'test-dup', active: () => false, what: 'y' })).toThrow(DuplicateConditionError)
		unregister()
	})
})

describe('the password vault', () => {
	afterEach(() => setVaultKeepsData(false))

	it('stays green while the vault is on but holds nothing', () => {
		expect(privacyStatus(withSettings({ passwordAutofill: true }))).toMatchObject({ traceless: true })
	})

	it('warns once the vault holds a login or a never-saved site', () => {
		setVaultKeepsData(true)
		expect(privacyStatus(withSettings({ passwordAutofill: true }))).toMatchObject({ traceless: false, detail: 'Saved passwords are written to disk.' })
	})

	it('names it beside the other things Zing keeps', () => {
		setVaultKeepsData(true)
		const status = privacyStatus(withSettings({ passwordAutofill: true, restoreTabs: true }))
		expect(status.detail).toBe('Your open tabs and saved passwords are written to disk.')
	})

	it('still warns once the setting is off, because the files stay on disk', () => {
		setVaultKeepsData(true)
		expect(privacyStatus(withSettings({ passwordAutofill: false }))).toMatchObject({ traceless: false, detail: 'Saved passwords are written to disk.' })
	})
})
