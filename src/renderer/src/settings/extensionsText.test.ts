import { describe, expect, it } from 'vitest'
import type { ExtensionRow } from '@shared/ipc'
import { DEFAULT_SETTINGS, type Settings } from '@shared/settings'
import { extensionDetail, loadUnpackedDetail, notRunningReason } from './extensionsText'

const settings = (patch: Partial<Settings>): Settings => ({ ...DEFAULT_SETTINGS, ...patch })
const BOTH_ON = settings({ persistentProfile: true, extensions: true })
const item = (patch: Partial<ExtensionRow> = {}): ExtensionRow => ({ key: 'k', name: 'Probe', version: '1.0', enabled: true, hasPopup: true, problem: null, ...patch })

describe('notRunningReason', () => {
	it('names Stay signed in first, since extensions do nothing without it', () => {
		expect(notRunningReason(settings({ persistentProfile: false, extensions: true }), false)).toBe('Extensions need Stay signed in to sites.')
	})

	it('names the switch, then the relaunch, then nothing once they run', () => {
		expect(notRunningReason(settings({ persistentProfile: true, extensions: false }), false)).toBe('Allow extensions is off.')
		expect(notRunningReason(BOTH_ON, false)).toBe('Relaunch Zing to run extensions.')
		expect(notRunningReason(BOTH_ON, true)).toBeNull()
	})

	it('a setting turned off since launch counts, though the extensions still run until the relaunch', () => {
		expect(notRunningReason(settings({ persistentProfile: true, extensions: false }), true)).toBe('Allow extensions is off.')
	})
})

describe('loadUnpackedDetail', () => {
	it('says only why loading is off while it is', () => {
		expect(loadUnpackedDetail(DEFAULT_SETTINGS, false)).toBe('Extensions need Stay signed in to sites.')
	})

	it('explains the copy, adding the relaunch while one is due', () => {
		expect(loadUnpackedDetail(BOTH_ON, true)).toBe('Pick an extension’s folder (the one with manifest.json). Zing copies it, so the original can move.')
		expect(loadUnpackedDetail(BOTH_ON, false)).toMatch(/so the original can move\. Relaunch Zing to run extensions\.$/)
	})
})

describe('extensionDetail', () => {
	it('never leaves an installed extension that does nothing looking fine', () => {
		expect(extensionDetail(item(), settings({ persistentProfile: false, extensions: true }), false)).toEqual({ text: 'Not running. Extensions need Stay signed in to sites.', warning: true })
	})

	it('shows a broken copy’s problem first', () => {
		expect(extensionDetail(item({ problem: 'broken' }), BOTH_ON, true)).toEqual({ text: 'broken', warning: true })
	})

	it('says where a running one’s popup is, and when it is off', () => {
		expect(extensionDetail(item(), BOTH_ON, true).text).toBe('On. Its popup opens from the extensions button in the toolbar.')
		expect(extensionDetail(item({ hasPopup: false }), BOTH_ON, true).text).toBe('On.')
		expect(extensionDetail(item({ enabled: false }), BOTH_ON, true)).toEqual({ text: 'Off.', warning: false })
	})
})
