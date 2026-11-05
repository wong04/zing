import { describe, expect, it } from 'vitest'
import { conversionFraction, durationOf, elapsedOf } from './ffmpegText'

describe('durationOf', () => {
	it('reads the length FFmpeg prints for its input', () => {
		expect(durationOf('  Duration: 00:02:00.04, start: 0.000000, bitrate: 8123 kb/s')).toBe(120.04)
		expect(durationOf('  Duration: 01:30:05.50, start: 0.0')).toBe(5405.5)
	})
	it('has none for a line without one, or for an unknown length', () => {
		expect(durationOf('frame=  100 fps=0.0')).toBeUndefined()
		expect(durationOf('  Duration: N/A, bitrate: N/A')).toBeUndefined()
	})
})

describe('elapsedOf', () => {
	it('reads how much has been made', () => {
		expect(elapsedOf('frame=  240 fps= 48 q=28.0 size=    1024kB time=00:00:10.00 bitrate= 838.9kbits/s speed=2.0x')).toBe(10)
		expect(elapsedOf('size=N/A time=00:01:01.50 bitrate=N/A')).toBe(61.5)
	})
	it('has none before the first frame', () => {
		expect(elapsedOf('size=       0kB time=N/A bitrate=N/A')).toBeUndefined()
		expect(elapsedOf('Stream #0:0: Video: hevc')).toBeUndefined()
	})
	it('ignores a negative start-up time', () => {
		expect(elapsedOf('time=-00:00:00.03 bitrate=N/A')).toBeUndefined()
	})
})

describe('conversionFraction', () => {
	it('is the part made of the whole, never past 1', () => {
		expect(conversionFraction(30, 120)).toBe(0.25)
		expect(conversionFraction(130, 120)).toBe(1)
	})
	it('is unknown without a length', () => {
		expect(conversionFraction(30, undefined)).toBeUndefined()
		expect(conversionFraction(30, 0)).toBeUndefined()
	})
})
