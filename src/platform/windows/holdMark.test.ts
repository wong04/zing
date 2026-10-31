import { describe, expect, it } from 'vitest'
import { holdMarkOfTheWeb, type MarkStore } from './holdMark'
import type { MarkOfTheWeb } from './security'

const ours: MarkOfTheWeb = { zoneId: 3, hostUrl: 'https://a.example/file.bin', referrerUrl: 'https://a.example/' }
const engine: MarkOfTheWeb = { zoneId: 3, hostUrl: 'about:internet' }

/** A fake stream plus a clock; Chromium writes its generic mark at `engineAt` ms. */
function scenario(engineAt: number | undefined): { store: MarkStore; sleep: (ms: number) => Promise<void>; final: () => MarkOfTheWeb | undefined } {
	let now = 0
	let mark: MarkOfTheWeb | undefined
	let engineWritten = false
	const tick = (): void => {
		if (engineAt !== undefined && !engineWritten && now >= engineAt) {
			mark = engine
			engineWritten = true
		}
	}
	return {
		store: {
			read: async () => {
				tick()
				return mark
			},
			write: async (m) => {
				tick()
				mark = m
			},
		},
		sleep: async (ms) => {
			now += ms
			tick()
		},
		final: () => mark,
	}
}

const options = (sleep: (ms: number) => Promise<void>) => ({ windowMs: 10_000, pollMs: 100, sleep })

describe('keeping our Mark of the Web over Chromium’s', () => {
	it('when Chromium writes before us', async () => {
		const s = scenario(0)
		await holdMarkOfTheWeb(s.store, ours, options(s.sleep))
		expect(s.final()).toEqual(ours)
	})
	it('when Chromium writes after a fixed 2 s wait would have given up (the race that lost HostUrl)', async () => {
		const s = scenario(2_500)
		await holdMarkOfTheWeb(s.store, ours, options(s.sleep))
		expect(s.final()).toEqual(ours)
	})
	it('when Chromium never writes', async () => {
		const s = scenario(undefined)
		await holdMarkOfTheWeb(s.store, ours, options(s.sleep))
		expect(s.final()).toEqual(ours)
	})
})
