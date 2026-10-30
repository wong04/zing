import { existsSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { TabRecord } from '@shared/model'
import { SettingsStore } from '@shared/settings'
import {
	SessionFileError,
	SessionSaver,
	browsingPartition,
	loadSession,
	queueRestore,
	takePendingRestore,
	toSessionTabs,
} from './session-restore'

let dir: string

beforeEach(() => {
	dir = mkdtempSync(join(tmpdir(), 'zing-session-'))
})
afterEach(() => rmSync(dir, { recursive: true, force: true }))

function record(id: string, url: string, changes: Partial<TabRecord> = {}): TabRecord {
	return { id, url, title: id, pinned: false, createdAt: 0, lastActiveAt: 0, updatedAt: 0, ...changes }
}

describe('browsingPartition', () => {
	it('is in-memory by default and on disk only with persistentProfile', () => {
		expect(browsingPartition(false)).toBe('zing-private')
		expect(browsingPartition(true)).toBe('persist:zing')
	})
})

describe('loadSession', () => {
	it('treats a missing file as a first run', () => {
		expect(loadSession(join(dir, 'none.json'))).toEqual([])
	})
	it('treats a corrupt file as no tabs', () => {
		const path = join(dir, 's.json')
		writeFileSync(path, '{{{not json')
		expect(loadSession(path)).toEqual([])
	})
	it('raises when the file exists but cannot be read', () => {
		expect(() => loadSession(dir)).toThrow(SessionFileError)
	})
})

describe('SessionSaver', () => {
	const records = [
		record('a', 'https://a.test/', { pinned: true }),
		record('b', 'https://b.test/'),
		record('c', 'about:blank'),
	]

	beforeEach(() => vi.useFakeTimers())
	afterEach(() => vi.useRealTimers())

	function enabledSettings(): SettingsStore {
		const settings = new SettingsStore()
		settings.set('restoreTabs', true)
		return settings
	}

	it('writes nothing while restoreTabs is off', () => {
		const path = join(dir, 'session.json')
		const saver = new SessionSaver(path, { activeId: 'b', records }, new SettingsStore())
		saver.changed()
		vi.advanceTimersByTime(1000)
		expect(existsSync(path)).toBe(false)
	})

	it('debounces writes and round-trips web tabs with the active one remembered', () => {
		const path = join(dir, 'session.json')
		const saver = new SessionSaver(path, { activeId: 'b', records }, enabledSettings())
		saver.changed()
		saver.changed()
		expect(existsSync(path)).toBe(false)
		vi.advanceTimersByTime(1000)
		expect(loadSession(path)).toEqual([
			{ url: 'https://a.test/', title: 'a', pinned: true, active: false },
			{ url: 'https://b.test/', title: 'b', pinned: false, active: true },
		])
	})

	it('flushes a pending write on demand', () => {
		const path = join(dir, 'session.json')
		const saver = new SessionSaver(path, { activeId: 'a', records }, enabledSettings())
		saver.changed()
		saver.flush()
		expect(readFileSync(path, 'utf8')).toContain('https://a.test/')
	})

	it('deletes the file when restoreTabs is turned off', () => {
		const path = join(dir, 'session.json')
		const settings = enabledSettings()
		const saver = new SessionSaver(path, { activeId: 'a', records }, settings)
		saver.changed()
		saver.flush()
		expect(existsSync(path)).toBe(true)
		settings.set('restoreTabs', false)
		expect(existsSync(path)).toBe(false)
		saver.changed()
		vi.advanceTimersByTime(1000)
		expect(existsSync(path)).toBe(false)
	})
})

describe('pending restore', () => {
	it('is handed to exactly one window', () => {
		queueRestore(toSessionTabs([record('a', 'https://a.test/')], 'a'))
		expect(takePendingRestore()).toHaveLength(1)
		expect(takePendingRestore()).toEqual([])
	})
})
