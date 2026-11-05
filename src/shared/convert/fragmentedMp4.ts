// Just enough of the ISO base media file format to feed a MediaSource from the fragmented MP4 that FFmpeg writes with
// `-movflags frag_keyframe+empty_moov+default_base_moof`: an init segment (ftyp, moov), then a moof and its mdat for each
// fragment, each starting at a keyframe. The bytes arrive in pieces, so every function says "not yet" (undefined) when a box
// is incomplete and throws only for bytes that can't be what FFmpeg wrote.

export class Mp4FormatError extends Error {
	override name = 'Mp4FormatError'
}

interface Header {
	readonly type: string
	/** The whole box, header included. */
	readonly size: number
}

const fourcc = (bytes: Uint8Array, at: number): string => String.fromCharCode(bytes[at]!, bytes[at + 1]!, bytes[at + 2]!, bytes[at + 3]!)

const u32 = (bytes: Uint8Array, at: number): number => ((bytes[at]! << 24) | (bytes[at + 1]! << 16) | (bytes[at + 2]! << 8) | bytes[at + 3]!) >>> 0

const u64 = (bytes: Uint8Array, at: number): number => u32(bytes, at) * 2 ** 32 + u32(bytes, at + 4)

/** The biggest box that is waited for whole; what arrives while waiting is kept in memory. */
const MAX_BOX = 256 * 1024 * 1024

function checked(type: string, size: number, headerSize: number): Header {
	if (!Number.isSafeInteger(size) || size < headerSize) throw new Mp4FormatError(`The ${JSON.stringify(type)} box has size ${size}, which can't hold its own ${headerSize}-byte header`)
	if (size > MAX_BOX) throw new Mp4FormatError(`The ${JSON.stringify(type)} box has size ${size}, more than the ${MAX_BOX} bytes the viewer will wait for`)
	return { type, size }
}

/** The box starting at `at`, or undefined while its header isn't all here. */
function header(bytes: Uint8Array, at: number): Header | undefined {
	if (at + 8 > bytes.length) return undefined
	const type = fourcc(bytes, at + 4)
	const size = u32(bytes, at)
	if (size === 1) {
		if (at + 16 > bytes.length) return undefined
		return checked(type, u64(bytes, at + 8), 16)
	}
	return checked(type, size, 8)
}

function find(bytes: Uint8Array, type: string, from = 0, to = bytes.length): number {
	const [a, b, c, d] = [...type].map((ch) => ch.charCodeAt(0)) as [number, number, number, number]
	for (let i = from; i + 4 <= to; i++) {
		if (bytes[i] === a && bytes[i + 1] === b && bytes[i + 2] === c && bytes[i + 3] === d) return i
	}
	return -1
}

/** How many bytes the init segment (ftyp and moov) takes, or undefined until the moov box is complete. */
export function initLength(bytes: Uint8Array): number | undefined {
	let at = 0
	for (;;) {
		const box = header(bytes, at)
		if (!box) return undefined
		if (at === 0 && box.type !== 'ftyp') throw new Mp4FormatError(`The converted file doesn't start with an ftyp box (it starts with ${JSON.stringify(box.type)})`)
		if (at + box.size > bytes.length) return undefined
		at += box.size
		if (box.type === 'moov') return at
	}
}

/** The codec string for `addSourceBuffer`, read from the init segment: `avc1.PPCCLL`, plus AAC when it has an audio track. */
export function codecsOf(init: Uint8Array): string {
	const avc = find(init, 'avcC')
	if (avc < 0 || avc + 8 > init.length) throw new Mp4FormatError('The converted file has no H.264 video track')
	const hex = (n: number): string => n.toString(16).padStart(2, '0')
	const video = `avc1.${hex(init[avc + 5]!)}${hex(init[avc + 6]!)}${hex(init[avc + 7]!)}`
	return find(init, 'mp4a') >= 0 ? `${video},mp4a.40.2` : video
}

/** Ticks per second of the first track, which is the one fragment start times are counted in. */
export function timescaleOf(init: Uint8Array): number {
	const at = find(init, 'mdhd')
	if (at < 0) throw new Mp4FormatError('The converted file has no timescale (mdhd box)')
	return init[at + 4] === 1 ? u32(init, at + 24) : u32(init, at + 16)
}

export type FragmentUnit =
	| { readonly kind: 'fragment'; readonly length: number; /** When it starts, in ticks of the track's timescale. */ readonly ticks: number }
	| { readonly kind: 'skip'; readonly length: number }

/**
 * The next thing at `from`: a moof with its mdat as one fragment, or a box to step over (the index at the end, free space).
 * Undefined while the whole of it hasn't arrived.
 */
export function nextFragment(bytes: Uint8Array, from: number): FragmentUnit | undefined {
	const first = header(bytes, from)
	if (!first) return undefined
	if (first.type !== 'moof') return from + first.size > bytes.length ? undefined : { kind: 'skip', length: first.size }
	const dataAt = from + first.size
	const data = header(bytes, dataAt)
	if (!data) return undefined
	if (data.type !== 'mdat') throw new Mp4FormatError(`A moof box is followed by ${JSON.stringify(data.type)}, not mdat`)
	const end = dataAt + data.size
	if (end > bytes.length) return undefined
	const tfdt = find(bytes, 'tfdt', from, dataAt)
	if (tfdt < 0) throw new Mp4FormatError('A fragment has no start time (tfdt box)')
	const ticks = bytes[tfdt + 4] === 1 ? u64(bytes, tfdt + 8) : u32(bytes, tfdt + 8)
	return { kind: 'fragment', length: end - from, ticks }
}
