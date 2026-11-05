import { describe, expect, it } from 'vitest'
import { describeEngine, describeOutcome, engineActions, type EngineInfo } from './engineInfo'

const ffmpeg: EngineInfo = {
	id: 'ffmpeg',
	name: 'FFmpeg',
	version: '9.0.2',
	url: 'https://example.test/ffmpeg.zip',
	licence: 'GPL v3',
	downloadBytes: 114_768_076,
	installedBytes: 0,
}

describe('describeEngine', () => {
	it('tells a person the size of the download before they start it', () => {
		expect(describeEngine(ffmpeg)).toBe('Not downloaded · 109.5 MB download')
	})
	it('shows the version and the room taken once it is on disk', () => {
		expect(describeEngine({ ...ffmpeg, installedVersion: '9.0.2', installedBytes: 400 * 1024 * 1024 })).toBe('Downloaded · version 9.0.2 · 400.0 MB on disk')
	})
})

describe('engineActions', () => {
	const installed: EngineInfo = { ...ffmpeg, installedVersion: '9.0.2', installedBytes: 1 }
	it('offers Download for an engine that is not here', () => {
		expect(engineActions(ffmpeg, { downloading: false, busy: false })).toEqual({ download: true, remove: false, cancel: false, update: false })
	})
	it('offers Remove for one that is, and Update only for yt-dlp', () => {
		expect(engineActions(installed, { downloading: false, busy: false })).toEqual({ download: false, remove: true, cancel: false, update: false })
		expect(engineActions({ ...installed, id: 'ytdlp' }, { downloading: false, busy: false }).update).toBe(true)
	})
	it('shows Cancel, and nothing else, on the row that is downloading', () => {
		expect(engineActions(ffmpeg, { downloading: true, busy: true })).toEqual({ download: false, remove: false, cancel: true, update: false })
	})
	it('keeps the other rows from starting a second job while one runs', () => {
		const other = engineActions({ ...installed, id: 'ytdlp' }, { downloading: false, busy: true })
		expect(other).toEqual({ download: false, remove: false, cancel: false, update: false })
	})
})

describe('ensureOutcome', () => {
	it('words a cancel as a plain fact, not an error', () => {
		expect(describeOutcome('cancelled')).toBe('Cancelled. Nothing was kept.')
		expect(describeOutcome('installed')).toBeNull()
	})
})
