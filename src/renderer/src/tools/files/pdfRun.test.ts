import { describe, expect, it } from 'vitest'
import { OPENING, describeCancelledPages, describeRun, describeStoppedOpen, notAdded, openInTurn, runParts, whyNotNow } from './pdfRun'

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

describe('opening a file while a run is in progress', () => {
	it('is refused with the reason, since it would end the session the run saves into', () => {
		expect(whyNotNow(null)).toBeNull()
		expect(whyNotNow('Saving…')).toBe('A save is running (Saving…). Wait for it to finish, or press Cancel, before opening or adding a file.')
	})
	it('says a file is still opening when that is what is busy, not that a save is running', () => {
		expect(whyNotNow(OPENING)).toBe('A file is still opening. Wait for it to finish, then try again.')
	})
	it('names the files that were not added, so a refused drop or hand-off is not lost without a word', () => {
		expect(notAdded(['a.pdf'], 'A file is still opening.')).toBe('a.pdf was not added. A file is still opening.')
		expect(notAdded(['a.pdf', 'b.pdf'], 'A file is still opening.')).toBe('a.pdf and b.pdf were not added. A file is still opening.')
	})
})

describe('what Convert says when pages were turned into pictures and the person cancelled', () => {
	it('says cancelled, and how many pages are already in Downloads', () => {
		expect(describeCancelledPages(0)).toBe('Cancelled. Nothing was kept.')
		expect(describeCancelledPages(undefined)).toBe('Cancelled. Nothing was kept.')
		expect(describeCancelledPages(1)).toBe('Cancelled. The 1 page already made is in your Downloads folder.')
		expect(describeCancelledPages(4)).toBe('Cancelled. The 4 pages already made are in your Downloads folder.')
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

describe('opening several files can be cancelled', () => {
	it('opens every file when nothing stops it', async () => {
		const opened: string[] = []
		const out = await openInTurn(['a', 'b', 'c'], async (f) => void opened.push(f), () => false)
		expect(opened).toEqual(['a', 'b', 'c'])
		expect(out).toEqual({ added: 3, stopped: false })
	})
	it('opens no further file once cancelled', async () => {
		let stop = false
		const opened: string[] = []
		const out = await openInTurn(
			['a', 'b', 'c', 'd'],
			async (f) => {
				opened.push(f)
				if (f === 'b') stop = true
			},
			() => stop,
		)
		expect(opened).toEqual(['a', 'b'])
		expect(out).toEqual({ added: 2, stopped: true })
	})
	it('counts a file whose opening Cancel broke off as not added, and does not throw', async () => {
		let stop = false
		const out = await openInTurn(
			['a', 'b', 'c'],
			async (f) => {
				if (f !== 'b') return
				stop = true
				throw new Error('aborted')
			},
			() => stop,
		)
		expect(out).toEqual({ added: 1, stopped: true })
	})
	it('throws a failure that Cancel did not cause', async () => {
		const run = openInTurn(['a', 'b'], async (f) => {
			if (f === 'b') throw new Error('b is damaged')
		}, () => false)
		await expect(run).rejects.toThrow('b is damaged')
	})
	it('says how many files were added before the stop', () => {
		expect(describeStoppedOpen(0)).toBe('Stopped. No file was added.')
		expect(describeStoppedOpen(1)).toBe('Stopped. 1 file was added.')
		expect(describeStoppedOpen(2)).toBe('Stopped. 2 files were added.')
	})
})
