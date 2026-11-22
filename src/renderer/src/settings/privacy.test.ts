import { afterEach, describe, expect, it } from 'vitest'
import { DEFAULT_SETTINGS, type Settings } from '@shared/settings'
import { DuplicateConditionError, privacyStatus, registerStoresDataCondition, setCrashLogKept, setLibraryHoldsImports, setLibraryKeepsIcons, setSiteGrantsKept, setVaultKeepsData } from './privacy'

// Reopen tabs is on by default; turning it off is the state where Zing keeps nothing.
const NOTHING_KEPT: Settings = { ...DEFAULT_SETTINGS, restoreTabs: false }
const withSettings = (patch: Partial<Settings>): Settings => ({ ...NOTHING_KEPT, ...patch })

describe('privacyStatus', () => {
	it('warns with the defaults, because Reopen tabs is on and writes the open tabs to disk', () => {
		expect(privacyStatus(DEFAULT_SETTINGS)).toMatchObject({ traceless: false, detail: 'Your open tabs are written to disk.' })
	})

	it('is green with Reopen tabs off: Zing keeps nothing', () => {
		expect(privacyStatus(NOTHING_KEPT)).toMatchObject({ traceless: true })
	})

	it('says while green that a bookmark keeps its icon only once its page is visited', () => {
		expect(privacyStatus(NOTHING_KEPT).detail).toContain("A bookmark's site icon is saved only once you visit that page.")
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
		expect(privacyStatus(NOTHING_KEPT).traceless).toBe(true)
		vaultHasEntries = true
		expect(privacyStatus(NOTHING_KEPT)).toMatchObject({ traceless: false, detail: 'Saved passwords are written to disk.' })
		unregister()
		expect(privacyStatus(NOTHING_KEPT).traceless).toBe(true)
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

describe('imported bookmarks', () => {
	it('imported bookmarks flip the banner and clearing them restores it', () => {
		setLibraryHoldsImports(true)
		expect(privacyStatus(NOTHING_KEPT)).toMatchObject({ traceless: false, detail: 'Imported bookmarks are written to disk.' })
		setLibraryHoldsImports(false)
		expect(privacyStatus(NOTHING_KEPT).traceless).toBe(true)
	})
})

describe('bookmark icons', () => {
	afterEach(() => setLibraryKeepsIcons(false))

	it('a saved bookmark icon flips the banner and names it, and none left restores it', () => {
		setLibraryKeepsIcons(true)
		expect(privacyStatus(NOTHING_KEPT)).toMatchObject({ traceless: false, detail: 'Icons of bookmarked sites you visited are written to disk.' })
		setLibraryKeepsIcons(false)
		expect(privacyStatus(NOTHING_KEPT).traceless).toBe(true)
	})
})

describe('crash log', () => {
	afterEach(() => setCrashLogKept(false))

	it('a crash log on disk flips the banner and says it holds only times and exit codes, and clearing it restores the banner', () => {
		setCrashLogKept(true)
		expect(privacyStatus(NOTHING_KEPT)).toMatchObject({ traceless: false, detail: 'A crash log (times and exit codes only) is written to disk.' })
		setCrashLogKept(false)
		expect(privacyStatus(NOTHING_KEPT).traceless).toBe(true)
	})
})

describe('remembered notification grants', () => {
	afterEach(() => setSiteGrantsKept(false))

	it('a remembered site flips the banner and names it, and none left restores it', () => {
		setSiteGrantsKept(true)
		expect(privacyStatus(NOTHING_KEPT)).toMatchObject({ traceless: false, detail: 'Sites allowed to show notifications are written to disk.' })
		setSiteGrantsKept(false)
		expect(privacyStatus(NOTHING_KEPT).traceless).toBe(true)
	})
})

describe('AI control', () => {
	it('AI control on turns the banner to a warning', () => {
		expect(privacyStatus(withSettings({ aiControlEnabled: true }))).toMatchObject({
			traceless: false,
			detail: 'An access token for AI control is written to disk.',
		})
		expect(privacyStatus(withSettings({ aiControlEnabled: false })).traceless).toBe(true)
	})
})
