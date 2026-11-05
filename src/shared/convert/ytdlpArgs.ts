import type { EngineJobSpec } from '../engines/engineJob'

// What the Download tool asks of yt-dlp. The renderer gives an address and a mode and nothing else: no folder, no program
// location (the host adds FFmpeg's), no free option text. The host's allow-list (EngineFlagPolicy.cs) accepts exactly
// these flags, and puts the address after a "--".

export type MediaMode = 'mp3' | 'mp4'

export class InvalidMediaUrlError extends Error {
	override name = 'InvalidMediaUrlError'
}

/** Prefers MP4 parts that merge without re-encoding, then anything; the host's list accepts this one string. */
const MP4_FORMAT = 'bv*[ext=mp4]+ba[ext=m4a]/b[ext=mp4]/bv*+ba/b'

/** yt-dlp picks its own file name and extension, so the name is fixed here and the host numbers it ("audio 2.mp3"). */
const OUTPUT_NAME: Readonly<Record<MediaMode, string>> = { mp3: 'audio.mp3', mp4: 'video.mp4' }

const HARD_LOCAL_SUFFIXES = ['.localhost', '.local', '.lan', '.internal', '.intranet', '.localdomain', '.home.arpa'] as const

const IPV4 = /^(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})$/

function isPrivateIpv4(a: number, b: number, c: number): boolean {
	if (a === 0 || a === 10 || a === 127 || a >= 224) return true
	if (a === 100 && b >= 64 && b <= 127) return true
	if (a === 169 && b === 254) return true
	if (a === 172 && b >= 16 && b <= 31) return true
	if (a === 192 && b === 168) return true
	if (a === 192 && b === 0 && c === 0) return true
	return a === 198 && (b === 18 || b === 19)
}

function ipv4Local(text: string): boolean | null {
	const match = IPV4.exec(text)
	if (!match) return null
	const [a, b, c] = [Number(match[1]), Number(match[2]), Number(match[3])]
	return isPrivateIpv4(a, b, c)
}

/** The eight 16-bit groups of an IPv6 address as the URL parser writes it (lower case, "::" for one run of zeros, no dotted tail). */
function ipv6Groups(text: string): number[] | null {
	const halves = text.split('::')
	if (halves.length > 2) return null
	const head = halves[0] === '' ? [] : halves[0]!.split(':')
	const tail = halves.length === 2 && halves[1] !== '' ? halves[1]!.split(':') : []
	const missing = 8 - head.length - tail.length
	if (halves.length === 1 ? missing !== 0 : missing < 1) return null
	const groups = [...head, ...Array<string>(halves.length === 2 ? missing : 0).fill('0'), ...tail].map((group) => Number.parseInt(group, 16))
	return groups.length === 8 && groups.every((group) => Number.isInteger(group) && group >= 0 && group <= 0xffff) ? groups : null
}

const embeddedIpv4 = (high: number, low: number): boolean => isPrivateIpv4(high >> 8, high & 0xff, low >> 8)

function ipv6Local(bracketed: string): boolean {
	const groups = ipv6Groups(bracketed.slice(1, -1))
	// Anything this can't read is refused rather than let through.
	if (!groups) return true
	const [first, second] = groups as [number, number]
	if (groups.every((group) => group === 0) || (groups.slice(0, 7).every((group) => group === 0) && groups[7] === 1)) return true
	if ((first & 0xfe00) === 0xfc00 || (first & 0xffc0) === 0xfe80 || (first & 0xff00) === 0xff00) return true
	// IPv4-mapped (::ffff:a.b.c.d) and NAT64 (64:ff9b::/96) carry an IPv4 address in the last 32 bits.
	const mapped = groups.slice(0, 5).every((group) => group === 0) && groups[5] === 0xffff
	const nat64 = first === 0x64 && second === 0xff9b && groups.slice(2, 6).every((group) => group === 0)
	if (mapped || nat64) return embeddedIpv4(groups[6]!, groups[7]!)
	// 6to4 (2002::/16) carries one in the next 32 bits.
	return first === 0x2002 && embeddedIpv4(second, groups[2]!)
}

function isLocalHost(hostname: string): boolean {
	if (hostname.startsWith('[')) return ipv6Local(hostname)
	const name = hostname.toLowerCase().replace(/\.$/, '')
	const ipv4 = ipv4Local(name)
	if (ipv4 !== null) return ipv4
	// A name with no dot is an intranet machine; the URL parser has already turned every numeric spelling into dotted form.
	if (!name.includes('.') || name === 'home.arpa') return true
	return HARD_LOCAL_SUFFIXES.some((suffix) => name.endsWith(suffix))
}

/**
 * The address to hand yt-dlp, cleaned: http or https only, no sign-in in it, nothing that starts with "-", and nothing
 * that points at this PC or the local network. A name that only resolves to a local address can't be told apart here.
 */
export function parseMediaUrl(text: string): string {
	const trimmed = text.trim()
	if (trimmed === '') throw new InvalidMediaUrlError('Paste a link to a video first.')
	// The URL parser silently drops tabs and line breaks, which would make "two addresses" look like one.
	// eslint-disable-next-line no-control-regex
	if (/[\u0000- \u007f]/.test(trimmed)) throw new InvalidMediaUrlError("That doesn't look like one link. Remove spaces and line breaks.")
	let url: URL
	try {
		url = new URL(trimmed)
	} catch {
		throw new InvalidMediaUrlError('That is not a web address. Start it with https://')
	}
	if (url.protocol !== 'http:' && url.protocol !== 'https:') throw new InvalidMediaUrlError('Only http and https links can be downloaded.')
	if (url.username !== '' || url.password !== '') throw new InvalidMediaUrlError("Links with a sign-in in them aren't accepted.")
	if (isLocalHost(url.hostname)) throw new InvalidMediaUrlError("Links to this PC or your local network aren't accepted.")
	return url.href
}

/** The job for one video. The playlist and channel a link may belong to are never fetched (--no-playlist). */
export function ytdlpJob(url: string, mode: MediaMode): EngineJobSpec {
	const address = parseMediaUrl(url)
	const common = ['--newline', '--no-playlist'] as const
	const args =
		mode === 'mp3'
			? [...common, '-x', '--audio-format', 'mp3', '--audio-quality', '0', '-o', { output: 0 }]
			: [...common, '-f', MP4_FORMAT, '--merge-output-format', 'mp4', '-o', { output: 0 }]
	return { args, outputs: [OUTPUT_NAME[mode]], urls: [address] }
}

const DOWNLOAD_PERCENT = /^\[download\]\s+(\d{1,3}(?:\.\d+)?)%/

/** The percentage in a "[download]  42.0% of ..." line, or null for any other line. */
export function parseProgress(line: string): number | null {
	const match = DOWNLOAD_PERCENT.exec(line)
	if (!match) return null
	const percent = Number(match[1])
	return percent <= 100 ? percent : null
}

/** The line yt-dlp prints once the download is in and it is converting or merging. */
export function isFinishingLine(line: string): boolean {
	return line.startsWith('[ExtractAudio]') || line.startsWith('[Merger]') || line.startsWith('[VideoRemuxer]')
}
