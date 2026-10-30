// yt-dlp: arguments, progress and failures. Ported from Krackn's
// DownloadView.swift and DownloadErrors.swift. Pure on purpose: the failure
// classification decides whether Zing silently retries, offers an update, or
// just reports, so it's the part worth testing.

export type MediaMode = 'video' | 'audio'

export function ytdlpArgs(mode: MediaMode, outputTemplate: string, url: string): string[] {
	const args = [
		// One progress line per update, not \r spam.
		'--newline',
		'--no-playlist',
		'-o',
		outputTemplate,
	]
	if (mode === 'audio') args.push('-x', '--audio-format', 'mp3', '--audio-quality', '0')
	// Prefer a pre-merged mp4 so a missing ffmpeg doesn't block stitching
	// separate video and audio streams.
	if (mode === 'video') args.push('-f', 'best[ext=mp4]/bestvideo+bestaudio/best')
	args.push(url)
	return args
}

// A 403 on the media URL almost always means the player client yt-dlp picked
// handed back links that don't validate; YouTube rotates this constantly.
// Asking for the default client costs one retry. Krackn measured that adding
// web_safari and tv made things worse ("requested format is not available"),
// so it's `default` only.
export function retryArgs(args: readonly string[]): string[] {
	return ['--extractor-args', 'youtube:player_client=default', ...args]
}

// Failures that mean "the extractor is out of date", as opposed to a bad URL
// or a private video. Offering "update yt-dlp" for a private video teaches
// people to ignore the suggestion, and then it's useless on the day it would
// have worked.
export function looksLikeExtractorBreakage(stderr: string): boolean {
	const text = stderr.toLowerCase()
	// Checked first: ordinary refusals no update can fix, several of which
	// mention a status code that would otherwise match.
	const benign = ['private video', 'video unavailable', 'members-only', 'is not a valid url', 'unsupported url', 'no space left', 'sign in to confirm your age']
	if (benign.some((s) => text.includes(s))) return false
	const signals = ['403', 'forbidden', 'unable to download video data', 'unable to extract', 'nsig extraction failed', 'signature extraction failed', 'unable to extract player response', 'requested format is not available']
	return signals.some((s) => text.includes(s))
}

// Turns yt-dlp's last line into something a person can act on.
export function explainFailure(stderr: string, exitCode: number): string {
	const text = stderr.toLowerCase()
	if (text.includes('private video')) return 'This video is private.'
	if (text.includes('video unavailable')) return "This video isn't available."
	if (text.includes('sign in to confirm your age') || text.includes('age-restricted')) {
		return "This video is age-restricted, so it can't be downloaded without signing in."
	}
	if (text.includes('no space left')) return 'The disk is full.'
	if (text.includes('is not a valid url') || text.includes('unsupported url')) return "That link isn't one yt-dlp recognises."
	if (text.includes('403') || text.includes('forbidden')) {
		return 'YouTube refused the download (HTTP 403). This nearly always means yt-dlp needs updating: YouTube changes how it signs media links every few weeks, and an older copy stops being able to follow them.'
	}
	if (text.includes('ffmpeg')) return 'This needs FFmpeg to finish. Install it from Convert → Set Up Tools.'
	const lines = stderr.split(/\r?\n/).filter((l) => l.trim() !== '')
	return lines.at(-1) ?? `yt-dlp exited with code ${exitCode}.`
}

export interface DownloadProgress {
	percent: number
	totalBytes?: number
	speedBytesPerSecond?: number
	etaSeconds?: number
}

const UNITS: Record<string, number> = { B: 1, KiB: 1024, MiB: 1024 ** 2, GiB: 1024 ** 3, TiB: 1024 ** 4, KB: 1e3, MB: 1e6, GB: 1e9 }

function bytes(amount: string, unit: string): number | undefined {
	const scale = UNITS[unit]
	return scale === undefined ? undefined : Number(amount) * scale
}

// New in Zing: Krackn showed the raw log; this lets the UI draw a real bar.
// "[download]  42.3% of ~ 12.34MiB at 1.23MiB/s ETA 00:10 (frag 3/9)"
export function parseProgress(line: string): DownloadProgress | undefined {
	const match = /^\[download\]\s+([\d.]+)%(?:\s+of\s+~?\s*([\d.]+)(\w+))?(?:\s+at\s+([\d.]+)(\w+)\/s)?(?:\s+ETA\s+([\d:]+))?/.exec(line.trim())
	if (!match) return undefined
	const [, percent, total, totalUnit, speed, speedUnit, eta] = match
	const progress: DownloadProgress = { percent: Number(percent) }
	if (total && totalUnit) progress.totalBytes = bytes(total, totalUnit)
	if (speed && speedUnit) progress.speedBytesPerSecond = bytes(speed, speedUnit)
	if (eta) progress.etaSeconds = eta.split(':').reduce((sum, part) => sum * 60 + Number(part), 0)
	return progress
}
