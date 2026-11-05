import { readFileSync } from 'node:fs'
import { describe, expect, it } from 'vitest'
import { explainMediaFailure, looksLikeExtractorBreakage } from './ytdlpErrors'
import { InvalidMediaUrlError, parseMediaUrl, parseProgress, ytdlpJob } from './ytdlpArgs'

// Shared with the host's EngineArguments.Urls tests: the same addresses are refused on both sides.
const vectors = JSON.parse(readFileSync(new URL('./mediaUrl.vectors.json', import.meta.url), 'utf8')) as { refuse: string[]; accept: string[] }

const watch = 'https://www.youtube.com/watch?v=dQw4w9WgXcQ'

describe('ytdlpJob', () => {
	it('gives the address as a url for the host to put after "--", never as an argument', () => {
		const job = ytdlpJob(watch, 'mp3')
		expect(job.urls).toEqual([watch])
		expect(JSON.stringify(job.args)).not.toContain('youtube')
	})
	it('extracts MP3 with -x --audio-format mp3 into the job output', () => {
		const job = ytdlpJob(watch, 'mp3')
		expect(job.args).toEqual(['--newline', '--no-playlist', '-x', '--audio-format', 'mp3', '--audio-quality', '0', '-o', { output: 0 }])
		expect(job.outputs).toEqual(['audio.mp3'])
	})
	it('merges MP4 into the job output', () => {
		const job = ytdlpJob(watch, 'mp4')
		expect(job.args).toEqual(['--newline', '--no-playlist', '-f', 'bv*[ext=mp4]+ba[ext=m4a]/b[ext=mp4]/bv*+ba/b', '--merge-output-format', 'mp4', '-o', { output: 0 }])
		expect(job.outputs).toEqual(['video.mp4'])
	})
	it('never carries a path, a cookie option or a browser identity', () => {
		for (const mode of ['mp3', 'mp4'] as const) {
			const text = JSON.stringify(ytdlpJob(watch, mode).args)
			for (const forbidden of ['cookies', 'impersonate', 'user-agent', 'exec', 'ffmpeg-location', 'C:', '\\\\']) expect(text).not.toContain(forbidden)
		}
	})
	it('refuses an address that starts with "-" instead of passing it on', () => {
		expect(() => ytdlpJob('--exec calc', 'mp3')).toThrow(InvalidMediaUrlError)
		expect(() => ytdlpJob('-o x', 'mp4')).toThrow(InvalidMediaUrlError)
	})
})

describe('parseMediaUrl', () => {
	it('accepts http and https and returns the cleaned address', () => {
		expect(parseMediaUrl('  https://youtu.be/dQw4w9WgXcQ  ')).toBe('https://youtu.be/dQw4w9WgXcQ')
		expect(parseMediaUrl('http://example.com/a')).toBe('http://example.com/a')
	})
	it.each(['', '   ', 'youtube.com/watch?v=1', 'ftp://example.com/a', 'file:///C:/x.mp4', 'javascript:alert(1)', 'data:text/plain,hi', 'https://', 'not a url'])('refuses %j', (text) => {
		expect(() => parseMediaUrl(text)).toThrow(InvalidMediaUrlError)
	})
	it('refuses an address with a line break or other control character in it', () => {
		expect(() => parseMediaUrl('https://example.com/a\nhttps://example.org/')).toThrow(InvalidMediaUrlError)
		expect(() => parseMediaUrl('https://example.com/a\u0000b')).toThrow(InvalidMediaUrlError)
		expect(() => parseMediaUrl('https://example.com/a b')).toThrow(InvalidMediaUrlError)
	})
	it('refuses a sign-in embedded in the address', () => {
		expect(() => parseMediaUrl('https://user:secret@example.com/a')).toThrow(InvalidMediaUrlError)
	})
	it.each(vectors.refuse)('refuses the local address %s', (text) => {
		expect(() => parseMediaUrl(text)).toThrow(InvalidMediaUrlError)
	})
	it.each(vectors.accept)('accepts the public address %s', (text) => {
		expect(parseMediaUrl(text)).toBe(new URL(text).href)
	})
})

describe('parseProgress', () => {
	it('reads the percentage of a download line', () => {
		expect(parseProgress('[download]  42.0% of   10.00MiB at    1.00MiB/s ETA 00:05')).toBe(42)
		expect(parseProgress('[download] 100% of 5.00MiB in 00:00:03 at 1.50MiB/s')).toBe(100)
		expect(parseProgress('[download]   0.0% of ~  10.00MiB at  Unknown B/s ETA Unknown')).toBe(0)
		expect(parseProgress('[download]   7.5% of ~  10.00MiB')).toBe(7.5)
	})
	it('is null for any other line', () => {
		expect(parseProgress('[download] Destination: audio.webm')).toBeNull()
		expect(parseProgress('[youtube] Extracting URL: https://x')).toBeNull()
		expect(parseProgress('[ExtractAudio] Destination: audio.mp3')).toBeNull()
		expect(parseProgress('')).toBeNull()
		expect(parseProgress('[download] 150% of x')).toBeNull()
	})
})

describe('yt-dlp failures (ported from DownloadErrors.swift)', () => {
	it('says a private video is private, and offers no update', () => {
		expect(explainMediaFailure('ERROR: [youtube] x: Private video. Sign in', 1)).toEqual({ message: 'This video is private.', offerUpdate: false })
	})
	it('says an unavailable video is unavailable', () => {
		expect(explainMediaFailure('ERROR: [youtube] x: Video unavailable', 1).message).toBe("This video isn't available.")
	})
	it('says an age-restricted video needs a sign-in, without offering one', () => {
		const failure = explainMediaFailure('ERROR: [youtube] x: Sign in to confirm your age. This video may be inappropriate', 1)
		expect(failure.message).toBe("This video is age-restricted, so it can't be downloaded without signing in.")
		expect(failure.offerUpdate).toBe(false)
	})
	it('says the disk is full', () => {
		expect(explainMediaFailure('OSError: [Errno 28] No space left on device', 1)).toEqual({ message: 'The disk is full.', offerUpdate: false })
	})
	it('says a link yt-dlp does not know is not recognised', () => {
		expect(explainMediaFailure('ERROR: Unsupported URL: https://example.com/', 1).message).toBe("That link isn't one yt-dlp recognises.")
	})
	it('offers the update for a 403, which is what a newer yt-dlp fixes', () => {
		const failure = explainMediaFailure('ERROR: unable to download video data: HTTP Error 403: Forbidden', 1)
		expect(failure.message).toContain('HTTP 403')
		expect(failure.message).toContain('yt-dlp needs updating')
		expect(failure.offerUpdate).toBe(true)
	})
	it('offers the update for the other signs of an out-of-date extractor', () => {
		for (const stderr of ['ERROR: [youtube] x: nsig extraction failed: Some formats may be missing', 'ERROR: Unable to extract player response', 'ERROR: Requested format is not available']) {
			expect(explainMediaFailure(stderr, 1).offerUpdate, stderr).toBe(true)
		}
	})
	it('says FFmpeg could not be found even though Zing requires it before starting', () => {
		const message = explainMediaFailure('ERROR: ffprobe and ffmpeg not found. Please install or provide the path using --ffmpeg-location', 1).message
		expect(message).toContain("couldn't find FFmpeg")
		expect(message).toContain('Settings, Files, Engines')
		expect(message).not.toContain('--ffmpeg-location')
	})
	it('never shows cookie advice from yt-dlp, whatever its last line says', () => {
		for (const line of ['ERROR: [site] x: Login required. Use --cookies-from-browser or --cookies for the authentication.', 'ERROR: This content needs --cookies']) {
			const failure = explainMediaFailure(`WARNING: a
${line}
`, 1)
			expect(failure.message).toBe("This site needs a sign-in. Zing doesn't use your browser's sign-ins.")
			expect(failure.offerUpdate).toBe(false)
		}
	})
	it('reads 403 only as error text, never from a video id', () => {
		expect(explainMediaFailure('ERROR: [youtube] a-403-forbidden-b: Video unavailable', 1).message).toBe("This video isn't available.")
		expect(explainMediaFailure('ERROR: [youtube] a-403-b: something odd', 1)).toEqual({ message: '[youtube] a-403-b: something odd', offerUpdate: false })
		expect(looksLikeExtractorBreakage('[download] Downloading a_403_b')).toBe(false)
		expect(looksLikeExtractorBreakage('ERROR: HTTP Error 403: Forbidden')).toBe(true)
		expect(looksLikeExtractorBreakage('ERROR: 403 Forbidden')).toBe(true)
	})
	it('does not name YouTube for another site', () => {
		expect(explainMediaFailure('ERROR: HTTP Error 403: Forbidden', 1).message).not.toContain('YouTube')
	})
	it.each(['ERROR: HTTP Error 429: Too Many Requests', 'ERROR: Please solve this CAPTCHA to continue', 'ERROR: Just a moment... Cloudflare', 'ERROR: Attention Required! | Cloudflare'])('treats %s as a bot check, with no update and no advice to get round it', (stderr) => {
		const failure = explainMediaFailure(stderr, 1)
		expect(failure.message).toContain('not a bot')
		expect(failure.message).toContain("Zing doesn't work around")
		expect(failure.offerUpdate).toBe(false)
		expect(looksLikeExtractorBreakage(stderr)).toBe(false)
	})
	it('reports a bot check in plain words and does not suggest a way round it', () => {
		const stderr = "ERROR: [youtube] x: Sign in to confirm you\u2019re not a bot. Use --cookies-from-browser or --cookies for the authentication."
		const failure = explainMediaFailure(stderr, 1)
		expect(failure.message).toContain("not a bot")
		expect(failure.message).toContain("Zing doesn't work around")
		expect(failure.message).not.toContain('cookies')
		expect(failure.offerUpdate).toBe(false)
		expect(explainMediaFailure("Sign in to confirm you're not a bot", 1).offerUpdate).toBe(false)
	})
	it('does not read a bot check as a 403, whatever else the output says', () => {
		const failure = explainMediaFailure("HTTP Error 403: Forbidden\nERROR: Sign in to confirm you're not a bot", 1)
		expect(failure.offerUpdate).toBe(false)
		expect(failure.message).toContain('not a bot')
	})
	it('falls back to the last line, then to the exit code', () => {
		expect(explainMediaFailure('WARNING: a\nERROR: something odd happened\n', 1).message).toBe('something odd happened')
		expect(explainMediaFailure('', 2).message).toBe('yt-dlp exited with code 2.')
	})
	it('looksLikeExtractorBreakage ignores ordinary refusals even when they mention a status code', () => {
		expect(looksLikeExtractorBreakage('Private video. HTTP Error 403')).toBe(false)
		expect(looksLikeExtractorBreakage('HTTP Error 403: Forbidden')).toBe(true)
		expect(looksLikeExtractorBreakage('no problem at all')).toBe(false)
	})
})
