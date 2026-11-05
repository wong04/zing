// Port of Krackn's DownloadErrors.swift: reading yt-dlp's failures. Pure, so the choice of whether to offer an update is testable.

export interface MediaFailure {
	/** In plain words, for the panel. */
	readonly message: string
	/** The extractor looks out of date: the panel offers the Update button (never updates by itself). */
	readonly offerUpdate: boolean
}

/** Ordinary refusals that no update can fix; several mention a status code that would otherwise look like breakage. */
const BENIGN = ['private video', 'video unavailable', 'members-only', 'is not a valid url', 'unsupported url', 'no space left', 'sign in to confirm your age']

const BREAKAGE = [
	'403',
	'forbidden',
	'unable to download video data',
	'unable to extract',
	'nsig extraction failed',
	'signature extraction failed',
	'unable to extract player response',
	'requested format is not available',
]

/** The wording of YouTube's bot check, with a straight or curly apostrophe. */
const BOT_CHECK = /confirm you.{0,3}re not a bot/

export function isBotCheck(stderr: string): boolean {
	return BOT_CHECK.test(stderr.toLowerCase())
}

/** An out-of-date extractor, as opposed to a bad link or a private video. A newer yt-dlp fixes only these. */
export function looksLikeExtractorBreakage(stderr: string): boolean {
	const text = stderr.toLowerCase()
	if (isBotCheck(text) || BENIGN.some((phrase) => text.includes(phrase))) return false
	return BREAKAGE.some((signal) => text.includes(signal))
}

function lastLine(stderr: string): string | null {
	const lines = stderr.split('\n').map((line) => line.trim()).filter((line) => line !== '')
	const last = lines.at(-1)
	return last === undefined ? null : last.replace(/^ERROR:\s*/, '')
}

/** Turns yt-dlp's stderr into something a person can act on. */
export function explainMediaFailure(stderr: string, exitCode: number): MediaFailure {
	const text = stderr.toLowerCase()
	const plain = (message: string): MediaFailure => ({ message, offerUpdate: false })
	// Before everything: this is a refusal by the site, and nothing Zing does about it is a fix.
	if (isBotCheck(text)) {
		return plain("The site is asking to confirm this connection is not a bot. Zing doesn't work around that, so this download can't go ahead. Try again later, or watch it in a normal tab.")
	}
	if (text.includes('private video')) return plain('This video is private.')
	if (text.includes('video unavailable')) return plain("This video isn't available.")
	if (text.includes('sign in to confirm your age') || text.includes('age-restricted')) {
		return plain("This video is age-restricted, so it can't be downloaded without signing in.")
	}
	if (text.includes('no space left')) return plain('The disk is full.')
	if (text.includes('is not a valid url') || text.includes('unsupported url')) return plain("That link isn't one yt-dlp recognises.")
	if (text.includes('403') || text.includes('forbidden')) {
		return {
			message:
				'YouTube refused the download (HTTP 403). This nearly always means yt-dlp needs updating: YouTube changes how it signs media links every few weeks, and an older copy stops being able to follow them.',
			offerUpdate: true,
		}
	}
	if (text.includes('ffmpeg')) return plain('This needs FFmpeg to finish. Download it from Settings, Files, Engines.')
	return { message: lastLine(stderr) ?? `yt-dlp exited with code ${exitCode}.`, offerUpdate: looksLikeExtractorBreakage(stderr) }
}
