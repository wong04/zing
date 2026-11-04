import { describe, expect, it } from 'vitest'
import { describeEngine, type EngineInfo } from './engineInfo'

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
