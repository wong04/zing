import { codecsOf, initLength, nextFragment, timescaleOf } from '@shared/convert/fragmentedMp4'

// Plays the fragmented MP4 that FFmpeg is still writing (host/Zing.Core/Viewer/ViewerFiles.cs). A <video src> can't: it needs the
// file's length up front. So this feeds a MediaSource by `Range` fetches from https://view.zing/<token>, which answer 206 with
// bytes that exist, 204 while no new bytes are ready (ask again) and 416 at the true end.
//
// The fetches read the file in order and index each fragment by its byte offset and start time. A seek to a time that is
// no longer buffered restarts the reading at that fragment's offset; a seek past what is indexed keeps reading, appending
// nothing until the fragment that holds the time; a seek past what FFmpeg has made yet goes to the latest ready fragment.
// Only a window of the video around the playhead is kept buffered, so a long video doesn't fill memory.

const AHEAD_SECONDS = 60
const BEHIND_SECONDS = 30
const WAIT_MS = 300
const BACK_PRESSURE_MS = 250

export class StreamError extends Error {
	override name = 'StreamError'
}

export interface StreamOptions {
	readonly url: string
	/** The input's length in seconds, when FFmpeg has said it, so the seek bar spans the whole video while it is made. */
	readonly duration: () => number | undefined
	/** The stream failed (the file is gone, the conversion failed, the browser refused a piece). It is not called after `stop`. */
	readonly onError: (error: Error) => void
}

interface IndexedFragment {
	readonly offset: number
	readonly start: number
}

const sleep = (ms: number): Promise<void> => new Promise((resolve) => setTimeout(resolve, ms))

function join(a: Uint8Array, b: Uint8Array): Uint8Array {
	if (a.length === 0) return b
	const both = new Uint8Array(a.length + b.length)
	both.set(a, 0)
	both.set(b, a.length)
	return both
}

class StreamPlayer {
	private readonly source = new MediaSource()
	private readonly objectUrl = URL.createObjectURL(this.source)
	private buffer: SourceBuffer | undefined
	private init: Uint8Array | undefined
	private timescale = 1
	private readonly index: IndexedFragment[] = []
	private generation = 0
	private stopped = false
	// The next byte to ask for, and the unused tail of what came before it.
	private offset = 0
	private carry: Uint8Array = new Uint8Array(0)
	// While set, fragments are indexed but not appended until the one holding this time.
	private skipUntil: number | undefined
	private candidate: { readonly bytes: Uint8Array; readonly start: number } | undefined

	constructor(
		private readonly video: HTMLVideoElement,
		private readonly options: StreamOptions,
	) {}

	start(): void {
		this.video.addEventListener('seeking', this.onSeeking)
		this.video.addEventListener('error', this.onVideoError)
		this.source.addEventListener('sourceopen', () => void this.run(this.generation), { once: true })
		this.video.src = this.objectUrl
	}

	stop(): void {
		this.stopped = true
		this.generation++
		this.video.removeEventListener('seeking', this.onSeeking)
		this.video.removeEventListener('error', this.onVideoError)
		this.video.removeAttribute('src')
		this.video.load()
		URL.revokeObjectURL(this.objectUrl)
	}

	private readonly onVideoError = (): void => {
		this.fail(new StreamError(this.video.error?.message || 'The browser could not play the converted video'))
	}

	private fail(error: Error): void {
		if (!this.stopped) this.options.onError(error)
	}

	private live(generation: number): boolean {
		return !this.stopped && generation === this.generation
	}

	private async run(generation: number): Promise<void> {
		try {
			await this.pump(generation)
		} catch (error) {
			if (this.live(generation)) this.fail(error instanceof Error ? error : new StreamError(String(error)))
		}
	}

	private async pump(generation: number): Promise<void> {
		while (this.live(generation)) {
			await this.untilRoom(generation)
			if (!this.live(generation)) return
			const chunk = await this.fetchFrom(this.offset)
			if (!this.live(generation)) return
			if (chunk === 'end') {
				await this.finish(generation)
				return
			}
			if (chunk === 'wait') {
				await this.clampSeek(generation)
				if (this.live(generation)) await sleep(WAIT_MS)
				continue
			}
			this.offset += chunk.length
			this.carry = join(this.carry, chunk)
			await this.consume(generation)
		}
	}

	private async fetchFrom(offset: number): Promise<Uint8Array | 'wait' | 'end'> {
		const response = await fetch(this.options.url, { headers: { Range: `bytes=${offset}-` }, cache: 'no-store' })
		if (response.status === 204) return 'wait'
		if (response.status === 416) return 'end'
		if (response.status !== 206) throw new StreamError(`The converted video is gone (the viewer answered ${response.status})`)
		return new Uint8Array(await response.arrayBuffer())
	}

	private async consume(generation: number): Promise<void> {
		if (this.init === undefined) {
			const length = initLength(this.carry)
			if (length === undefined) return
			await this.setUp(this.carry.slice(0, length))
			if (!this.live(generation)) return
			this.carry = this.carry.slice(length)
		}
		let used = 0
		for (;;) {
			const unit = nextFragment(this.carry, used)
			if (!unit) break
			if (unit.kind === 'fragment') {
				const at = this.offset - this.carry.length + used
				await this.take(generation, at, this.carry.slice(used, used + unit.length), unit.ticks / this.timescale)
				if (!this.live(generation)) return
			}
			used += unit.length
		}
		this.carry = this.carry.slice(used)
	}

	private async setUp(init: Uint8Array): Promise<void> {
		const type = `video/mp4; codecs="${codecsOf(init)}"`
		if (!MediaSource.isTypeSupported(type)) throw new StreamError(`This computer can't play ${type}`)
		this.timescale = timescaleOf(init)
		this.buffer = this.source.addSourceBuffer(type)
		this.init = init
		const total = this.options.duration()
		if (total !== undefined && total > 0) this.source.duration = total
		await this.rawAppend(init)
	}

	private async take(generation: number, offset: number, bytes: Uint8Array, start: number): Promise<void> {
		if (!this.index.some((f) => f.offset === offset)) this.index.push({ offset, start })
		if (this.skipUntil !== undefined) {
			if (start <= this.skipUntil) {
				this.candidate = { bytes, start }
				return
			}
			const held = this.candidate
			this.candidate = undefined
			this.skipUntil = undefined
			if (held) await this.append(held.bytes)
			if (!this.live(generation)) return
		}
		await this.append(bytes)
	}

	/** Waits while far enough is buffered ahead of the playhead, unless a seek needs the reading to go on. */
	private async untilRoom(generation: number): Promise<void> {
		while (this.live(generation) && this.skipUntil === undefined && this.init !== undefined && this.aheadSeconds() > AHEAD_SECONDS) {
			await sleep(BACK_PRESSURE_MS)
		}
	}

	private aheadSeconds(): number {
		const at = this.video.currentTime
		const ranges = this.video.buffered
		for (let i = 0; i < ranges.length; i++) {
			if (ranges.start(i) <= at + 0.1 && ranges.end(i) >= at) return ranges.end(i) - at
		}
		return 0
	}

	private buffered(time: number): boolean {
		const ranges = this.video.buffered
		for (let i = 0; i < ranges.length; i++) {
			if (ranges.start(i) <= time && ranges.end(i) > time) return true
		}
		return false
	}

	private async append(bytes: Uint8Array): Promise<void> {
		await this.evict(this.video.currentTime - BEHIND_SECONDS)
		try {
			await this.rawAppend(bytes)
		} catch (error) {
			if (!(error instanceof DOMException) || error.name !== 'QuotaExceededError') throw error
			// Full: drop everything but the last few seconds behind the playhead and try once more.
			await this.evict(this.video.currentTime - 5)
			await this.rawAppend(bytes)
		}
	}

	private async idle(): Promise<void> {
		const buffer = this.buffer!
		while (buffer.updating) await new Promise<void>((resolve) => buffer.addEventListener('updateend', () => resolve(), { once: true }))
	}

	private async rawAppend(bytes: Uint8Array): Promise<void> {
		const buffer = this.buffer!
		await this.idle()
		buffer.appendBuffer(bytes as BufferSource)
		await this.untilUpdated(buffer)
	}

	private untilUpdated(buffer: SourceBuffer): Promise<void> {
		return new Promise<void>((resolve, reject) => {
			const done = (): void => {
				buffer.removeEventListener('error', failed)
				resolve()
			}
			const failed = (): void => {
				buffer.removeEventListener('updateend', done)
				reject(new StreamError('The browser refused a piece of the converted video'))
			}
			buffer.addEventListener('updateend', done, { once: true })
			buffer.addEventListener('error', failed, { once: true })
		})
	}

	private async evict(until: number): Promise<void> {
		const buffer = this.buffer!
		if (buffer.buffered.length === 0) return
		const first = buffer.buffered.start(0)
		if (until <= first) return
		await this.idle()
		buffer.remove(first, until)
		await this.untilUpdated(buffer)
	}

	private async finish(generation: number): Promise<void> {
		if (this.init === undefined) throw new StreamError('The converted video ended before it began')
		// A seek into the very last fragment is still waiting for a fragment after it that will never come.
		const held = this.candidate
		this.candidate = undefined
		this.skipUntil = undefined
		if (held) await this.append(held.bytes)
		if (!this.live(generation)) return
		await this.idle()
		if (this.source.readyState === 'open') this.source.endOfStream()
	}

	/** FFmpeg hasn't made the time that was asked for: go to the latest part that is ready instead. */
	private async clampSeek(generation: number): Promise<void> {
		if (this.skipUntil === undefined) return
		const held = this.candidate
		const latest = held?.start ?? this.index[this.index.length - 1]?.start ?? 0
		this.candidate = undefined
		this.skipUntil = undefined
		if (held) await this.append(held.bytes)
		if (!this.live(generation)) return
		this.video.currentTime = latest
	}

	private readonly onSeeking = (): void => {
		if (this.init === undefined) return
		const target = this.video.currentTime
		if (this.buffered(target)) return
		const earlier = this.index.filter((f) => f.start <= target)
		const last = this.index[this.index.length - 1]
		if (last !== undefined && target < last.start && earlier.length > 0) {
			this.restartAt(earlier[earlier.length - 1]!.offset)
			return
		}
		// A reader that has already reached the end starts again from the last fragment, which holds the time.
		if (this.source.readyState === 'ended' && last !== undefined) {
			this.restartAt(last.offset)
			return
		}
		this.skipUntil = target
		this.candidate = undefined
	}

	private restartAt(offset: number): void {
		const generation = ++this.generation
		this.offset = offset
		this.carry = new Uint8Array(0)
		this.skipUntil = undefined
		this.candidate = undefined
		// Stops an append in flight and clears the parser; not allowed (or needed) once the stream has ended, as the next append reopens it.
		if (this.source.readyState === 'open' && this.buffer) this.buffer.abort()
		void this.resume(generation)
	}

	/** After a seek: the init segment goes in again so the parser is in a known state, then the reading goes on from the new offset. */
	private async resume(generation: number): Promise<void> {
		try {
			await this.rawAppend(this.init!)
			await this.pump(generation)
		} catch (error) {
			if (this.live(generation)) this.fail(error instanceof Error ? error : new StreamError(String(error)))
		}
	}
}

/** Plays the growing file at `options.url` in `video`. Returns what stops it and frees everything it holds. */
export function playStream(video: HTMLVideoElement, options: StreamOptions): () => void {
	const player = new StreamPlayer(video, options)
	player.start()
	return () => player.stop()
}
