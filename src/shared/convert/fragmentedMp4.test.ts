import { describe, expect, it } from 'vitest'
import { codecsOf, initLength, nextFragment, timescaleOf } from './fragmentedMp4'

const ascii = (text: string): number[] => [...text].map((c) => c.charCodeAt(0))
const u32 = (n: number): number[] => [(n >>> 24) & 255, (n >>> 16) & 255, (n >>> 8) & 255, n & 255]

function box(type: string, ...parts: (number[] | Uint8Array)[]): Uint8Array {
	const body = parts.flatMap((p) => [...p])
	return Uint8Array.from([...u32(8 + body.length), ...ascii(type), ...body])
}

function join(...parts: Uint8Array[]): Uint8Array {
	return Uint8Array.from(parts.flatMap((p) => [...p]))
}

const avcC = (profile: number, compat: number, level: number): Uint8Array => box('avcC', [1, profile, compat, level, 0xff])
const mdhd = (timescale: number): Uint8Array => box('mdhd', [0, 0, 0, 0], u32(0), u32(0), u32(timescale), u32(0))
const mdhd64 = (timescale: number): Uint8Array => box('mdhd', [1, 0, 0, 0], u32(0), u32(0), u32(0), u32(0), u32(timescale), u32(0), u32(0))
const tfdt = (ticks: number): Uint8Array => box('tfdt', [0, 0, 0, 0], u32(ticks))
const tfdt64 = (ticks: number): Uint8Array => box('tfdt', [1, 0, 0, 0], u32(Math.floor(ticks / 2 ** 32)), u32(ticks % 2 ** 32))

const initSegment = (withAudio: boolean): Uint8Array =>
	join(box('ftyp', ascii('isom'), u32(512)), box('moov', box('trak', box('mdia', mdhd(12800), box('minf', avcC(0x64, 0x00, 0x28), ...(withAudio ? [box('mp4a', [0, 0])] : []))))))

const fragment = (ticks: number, payload = 5): Uint8Array => join(box('moof', box('traf', tfdt(ticks))), box('mdat', new Uint8Array(payload)))

describe('initLength', () => {
	it('is the end of the moov box', () => {
		const init = initSegment(true)
		expect(initLength(join(init, fragment(0)))).toBe(init.length)
	})
	it('waits while the moov box is incomplete', () => {
		const init = initSegment(true)
		expect(initLength(init.subarray(0, init.length - 1))).toBeUndefined()
		expect(initLength(new Uint8Array(3))).toBeUndefined()
	})
	it('refuses a file that is not an MP4 at all', () => {
		expect(() => initLength(Uint8Array.from([0, 0, 0, 16, ...ascii('RIFF'), 0, 0, 0, 0, 0, 0, 0, 0]))).toThrow(/ftyp/)
	})
})

describe('codecsOf', () => {
	it('reads the H.264 profile and level from avcC and adds AAC when there is audio', () => {
		expect(codecsOf(initSegment(true))).toBe('avc1.640028,mp4a.40.2')
	})
	it('leaves out the audio codec when the file has no audio track', () => {
		expect(codecsOf(initSegment(false))).toBe('avc1.640028')
	})
	it('pads each part to two hex digits', () => {
		expect(codecsOf(join(box('ftyp', ascii('isom')), box('moov', avcC(0x4d, 0x00, 0x0a))))).toBe('avc1.4d000a')
	})
	it('refuses an init segment with no H.264 video', () => {
		expect(() => codecsOf(join(box('ftyp', ascii('isom')), box('moov', box('trak'))))).toThrow(/H.264/)
	})
})

describe('timescaleOf', () => {
	it('reads a version 0 and a version 1 mdhd', () => {
		expect(timescaleOf(join(box('moov', mdhd(12800))))).toBe(12800)
		expect(timescaleOf(join(box('moov', mdhd64(90000))))).toBe(90000)
	})
	it('refuses an init segment without one', () => {
		expect(() => timescaleOf(box('moov'))).toThrow(/timescale/)
	})
})

describe('nextFragment', () => {
	it('returns a moof and its mdat as one fragment, with its start time in ticks', () => {
		const bytes = join(fragment(25600, 7), fragment(51200))
		const first = nextFragment(bytes, 0)
		expect(first).toEqual({ kind: 'fragment', length: fragment(25600, 7).length, ticks: 25600 })
		const second = nextFragment(bytes, first!.length)
		expect(second).toMatchObject({ kind: 'fragment', ticks: 51200 })
	})
	it('reads a 64-bit start time', () => {
		const big = 2 ** 33 + 5
		const bytes = join(box('moof', box('traf', tfdt64(big))), box('mdat', new Uint8Array(1)))
		expect(nextFragment(bytes, 0)).toMatchObject({ ticks: big })
	})
	it('waits for the whole fragment', () => {
		const bytes = fragment(0, 100)
		expect(nextFragment(bytes.subarray(0, bytes.length - 1), 0)).toBeUndefined()
		expect(nextFragment(bytes.subarray(0, 10), 0)).toBeUndefined()
		expect(nextFragment(new Uint8Array(0), 0)).toBeUndefined()
	})
	it('waits when the moof is there and the mdat has not started', () => {
		const moof = box('moof', box('traf', tfdt(0)))
		expect(nextFragment(moof, 0)).toBeUndefined()
	})
	it('skips boxes that are not part of a fragment, such as the index at the end', () => {
		const bytes = join(box('mfra', new Uint8Array(4)), fragment(0))
		expect(nextFragment(bytes, 0)).toEqual({ kind: 'skip', length: box('mfra', new Uint8Array(4)).length })
	})
	it('reads a box with a 64-bit size', () => {
		const payload = new Uint8Array(3)
		const big = Uint8Array.from([...u32(1), ...ascii('mdat'), ...u32(0), ...u32(16 + payload.length), ...payload])
		const moof = box('moof', box('traf', tfdt(10)))
		expect(nextFragment(join(moof, big), 0)).toMatchObject({ kind: 'fragment', length: moof.length + big.length })
	})
	it('refuses a fragment with no start time', () => {
		expect(() => nextFragment(join(box('moof', box('traf')), box('mdat', new Uint8Array(1))), 0)).toThrow(/tfdt/)
	})
	it('refuses a box whose size is smaller than its own header', () => {
		expect(() => nextFragment(Uint8Array.from([0, 0, 0, 4, ...ascii('free')]), 0)).toThrow(/size/)
	})
})
