// Port of Krackn's Tests/cancellation. Cancelling a fetch must return promptly
// instead of waiting out the timeout, and many simultaneous cancels must not
// deadlock.
//
// These hit live sites. Like Krackn's, the time limits are generous so a slow
// network doesn't fail them; the measured latencies are logged so a regression
// toward "waits out the timeout" is still obvious. Re-run before believing a failure.

import { beforeAll, describe, expect, it } from 'vitest'
import { normalize, RelayStrategy } from '../../shared/unblock/relayEngine'
import { CancelledError } from './abort'
import { dohReachable } from './liveNetwork'
import { pinnedFetch } from './pinnedFetch'
import { relayFetch } from './relayFetch'

const wifi = 'https://en.wikipedia.org/wiki/Wi-Fi'
const sleep = (ms: number): Promise<void> => new Promise((resolve) => setTimeout(resolve, ms))

type Outcome = 'cancelled' | 'completed' | 'failed'

async function settle(promise: Promise<unknown>): Promise<Outcome> {
	try {
		await promise
		return 'completed'
	} catch (error) {
		return error instanceof CancelledError ? 'cancelled' : 'failed'
	}
}

let dohUp = false
beforeAll(async () => {
	dohUp = await dohReachable()
}, 30_000)

describe('cancellation', () => {
	it.each([20, 150, 500])('relayFetch cancelled at %ims returns fast', async (delay) => {
		const controller = new AbortController()
		const started = Date.now()
		const pending = settle(relayFetch(wifi, { signal: controller.signal }))
		await sleep(delay)
		const cancelledAt = Date.now()
		controller.abort()
		const outcome = await pending
		const total = Date.now() - started
		console.info(`relayFetch cancel@${delay}ms: ${outcome}, total ${total}ms, abort-to-return ${Date.now() - cancelledAt}ms`)
		expect(total).toBeLessThan(12_000)
		if (outcome === 'cancelled') expect(Date.now() - cancelledAt).toBeLessThan(1_000)
	}, 30_000)

	it.each([1, 2, 3, 4, 5])('pinnedFetch cancel %i resumes', async () => {
		const controller = new AbortController()
		const started = Date.now()
		const pending = settle(pinnedFetch(normalize('https://en.wikipedia.org/wiki/Computer_network'), controller.signal, { timeoutMs: 60_000 }))
		await sleep(60)
		const cancelledAt = Date.now()
		controller.abort()
		const outcome = await pending
		console.info(`pinnedFetch cancel@60ms: ${outcome}, abort-to-return ${Date.now() - cancelledAt}ms`)
		expect(Date.now() - started).toBeLessThan(12_000)
		if (outcome === 'cancelled') expect(Date.now() - cancelledAt).toBeLessThan(1_000)
	}, 30_000)

	it('15 concurrent cancels do not deadlock', async () => {
		const started = Date.now()
		await Promise.all(
			Array.from({ length: 15 }, async () => {
				const controller = new AbortController()
				const pending = settle(pinnedFetch(normalize(wifi), controller.signal, { timeoutMs: 60_000 }))
				await sleep(20 + Math.random() * 280)
				controller.abort()
				await pending
			}),
		)
		console.info(`15 concurrent cancels took ${Date.now() - started}ms`)
		expect(Date.now() - started).toBeLessThan(30_000)
	}, 60_000)

	it('a normal fetch still works after all that', async (context) => {
		if (!dohUp) context.skip()
		const result = await relayFetch('https://example.com', {
			signal: new AbortController().signal,
			enabled: new Set([RelayStrategy.dohPinned]),
		})
		expect(result.rawBody.length).toBeGreaterThan(0)
	}, 60_000)

	it('an already-aborted signal rejects without touching the network', async () => {
		const controller = new AbortController()
		controller.abort()
		await expect(relayFetch('https://example.com', { signal: controller.signal })).rejects.toBeInstanceOf(CancelledError)
	})
})
