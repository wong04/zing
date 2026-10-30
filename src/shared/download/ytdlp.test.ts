// Ported from reference/krackn/Tests/unit/main.swift, "L. yt-dlp failures" (14 checks).
import { describe, expect, it } from 'vitest'
import { explainFailure, looksLikeExtractorBreakage, parseProgress, retryArgs, ytdlpArgs } from './ytdlp'

describe('stale extractor is recognised', () => {
	it.each([
		'ERROR: unable to download video data: HTTP Error 403: Forbidden',
		'ERROR: unable to extract player response',
		'WARNING: nsig extraction failed',
		'ERROR: requested format is not available',
		'HTTP Error 403: Forbidden',
	])('%s', (s) => expect(looksLikeExtractorBreakage(s)).toBe(true))
})

describe('ordinary refusals are not', () => {
	it.each([
		"ERROR: Private video. Sign in if you've been granted access",
		'ERROR: Video unavailable',
		"ERROR: 'x' is not a valid URL",
		'ERROR: No space left on device',
	])('%s', (s) => expect(looksLikeExtractorBreakage(s)).toBe(false))
})

describe('the message the user reads', () => {
	it('403 explained plainly', () => expect(explainFailure('HTTP Error 403: Forbidden', 1)).toContain('updating'))
	it('private video explained', () => expect(explainFailure('ERROR: Private video.', 1)).toContain('private'))
	it('age gate explained', () => expect(explainFailure('ERROR: Sign in to confirm your age', 1)).toContain('age-restricted'))
	it('disk full explained', () => expect(explainFailure('ERROR: No space left on device', 1)).toContain('disk is full'))
	it('unknown falls back to last line', () => expect(explainFailure('something\nweird happened', 3)).toBe('weird happened'))
	it('empty stderr falls back to the exit code', () => expect(explainFailure('', 3)).toBe('yt-dlp exited with code 3.'))
})

describe('arguments', () => {
	it('audio extracts best-quality mp3', () => {
		expect(ytdlpArgs('audio', 'C:\\D\\%(title)s.%(ext)s', 'https://y.example/v')).toEqual([
			'--newline', '--no-playlist', '-o', 'C:\\D\\%(title)s.%(ext)s', '-x', '--audio-format', 'mp3', '--audio-quality', '0', 'https://y.example/v',
		])
	})
	it('video prefers a pre-merged mp4', () => expect(ytdlpArgs('video', 'o', 'u')).toContain('best[ext=mp4]/bestvideo+bestaudio/best'))
	it('the retry only asks for the default player client', () => {
		expect(retryArgs(['--newline', 'u']).slice(0, 2)).toEqual(['--extractor-args', 'youtube:player_client=default'])
	})
})

describe('progress lines (new in Zing)', () => {
	it('parses percent, size, speed and ETA', () => {
		expect(parseProgress('[download]  42.3% of ~ 12.00MiB at  1.50MiB/s ETA 01:05 (frag 3/9)')).toEqual({
			percent: 42.3,
			totalBytes: 12 * 1024 ** 2,
			speedBytesPerSecond: 1.5 * 1024 ** 2,
			etaSeconds: 65,
		})
	})
	it('parses the final line', () => expect(parseProgress('[download] 100% of   3.20MiB in 00:00:02 at 1.43MiB/s')?.percent).toBe(100))
	it('ignores other lines', () => {
		expect(parseProgress('[youtube] abc: Downloading webpage')).toBeUndefined()
		expect(parseProgress('[download] Destination: x.mp4')).toBeUndefined()
	})
})
