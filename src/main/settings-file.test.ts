import { mkdtempSync, readdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { DEFAULT_SETTINGS, SettingsStore, changedSettings } from '@shared/settings'
import { DebouncedJsonWriter, SettingsFileError, loadSettingsFile } from './settings-file'

let dir: string
let file: string

beforeEach(() => {
	dir = mkdtempSync(join(tmpdir(), 'zing-settings-'))
	file = join(dir, 'nested', 'settings.json')
})

afterEach(() => {
	vi.useRealTimers()
	rmSync(dir, { recursive: true, force: true })
})

describe('settings file', () => {
	it('reads defaults on first run', () => {
		expect(loadSettingsFile(file)).toEqual(DEFAULT_SETTINGS)
	})

	it('round-trips a change through a debounced atomic write', () => {
		vi.useFakeTimers()
		const store = new SettingsStore(loadSettingsFile(file))
		const writer = new DebouncedJsonWriter(file, 300)
		store.subscribe((s) => writer.schedule(changedSettings(s)))
		store.set('tabBarPosition', 'right')
		store.set('restoreTabs', true)
		vi.advanceTimersByTime(299)
		expect(() => readFileSync(file)).toThrow()
		vi.advanceTimersByTime(1)

		expect(JSON.parse(readFileSync(file, 'utf8'))).toEqual({ tabBarPosition: 'right', restoreTabs: true })
		expect(readdirSync(join(dir, 'nested'))).toEqual(['settings.json'])
		const reloaded = loadSettingsFile(file)
		expect(reloaded.tabBarPosition).toBe('right')
		expect(reloaded.blockTrackers).toBe(true)
	})

	it('flush writes the pending change immediately', () => {
		const writer = new DebouncedJsonWriter(file, 10_000)
		writer.schedule({ confirmQuit: true })
		writer.flush()
		expect(loadSettingsFile(file).confirmQuit).toBe(true)
	})

	it('raises SettingsFileError for a corrupt or invalid file', () => {
		const writer = new DebouncedJsonWriter(file)
		writer.schedule({})
		writer.flush()
		writeFileSync(file, '{ not json')
		expect(() => loadSettingsFile(file)).toThrow(SettingsFileError)
		writeFileSync(file, JSON.stringify({ defaultQuality: 'high' }))
		expect(() => loadSettingsFile(file)).toThrow(SettingsFileError)
	})
})
