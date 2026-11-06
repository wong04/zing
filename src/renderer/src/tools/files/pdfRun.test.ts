import { describe, expect, it } from 'vitest'
import { describeRun, runParts } from './pdfRun'

const bytes = new Uint8Array([1])

describe('a run of saves can be cancelled', () => {
	it('saves every part when nothing stops it', async () => {
		const out = await runParts(3, async () => bytes, async (i) => `part ${i + 1}.pdf`, () => false)
		expect(out).toEqual({ saved: ['part 1.pdf', 'part 2.pdf', 'part 3.pdf'], total: 3, stopped: false })
	})
	it('makes no further part once cancelled, and counts the one that was already being saved', async () => {
		let stop = false
		const saved: number[] = []
		const out = await runParts(
			5,
			async () => bytes,
			async (i) => {
				saved.push(i)
				if (i === 1) stop = true
				return `p${i + 1}.pdf`
			},
			() => stop,
		)
		expect(saved).toEqual([0, 1])
		expect(out).toEqual({ saved: ['p1.pdf', 'p2.pdf'], total: 5, stopped: true })
	})
	it('treats a failure caused by cancelling as a stop, not an error', async () => {
		let stop = false
		const out = await runParts(
			3,
			async (i) => {
				if (i === 1) {
					stop = true
					throw new Error('Stopped')
				}
				return bytes
			},
			async (i) => `p${i + 1}.pdf`,
			() => stop,
		)
		expect(out).toEqual({ saved: ['p1.pdf'], total: 3, stopped: true })
	})
	it('rethrows another failure, keeping what was saved so far', async () => {
		const error = (await runParts(
			3,
			async (i) => {
				if (i === 1) throw new Error('disk full')
				return bytes
			},
			async (i) => `p${i + 1}.pdf`,
			() => false,
		).catch((e: unknown) => e)) as Error & { saved?: string[] }
		expect(error.message).toBe('disk full')
		expect(error.saved).toEqual(['p1.pdf'])
	})
})

describe('what the panel says about a run', () => {
	it('says exactly what was saved', () => {
		expect(describeRun({ saved: ['a.pdf', 'b.pdf'], total: 5, stopped: true })).toBe('Stopped. 2 of 5 files were saved to your Downloads folder; the other 3 were not made.')
		expect(describeRun({ saved: [], total: 5, stopped: true })).toBe('Stopped. Nothing was saved.')
		expect(describeRun({ saved: ['a.pdf'], total: 1, stopped: true })).toBe('Stopped. The file had already been saved to your Downloads folder.')
		expect(describeRun({ saved: ['a.pdf'], total: 1, stopped: false })).toBeNull()
	})
})
