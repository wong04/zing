// Live-network checks for diagnose(). Like the other network tests these are
// tolerant of slow links; the verdict itself depends on the network under
// test (a network that blocks DoH legitimately yields "inconclusive").

import { describe, expect, it } from 'vitest'
import { BlockKind } from '../../shared/unblock/blockDiagnostics'
import { CancelledError } from './abort'
import { diagnose } from './diagnose'

describe('diagnose', () => {
	it('reports the ladder for a real host', async () => {
		const report = await diagnose('example.com', { signal: new AbortController().signal })
		expect(report.host).toBe('example.com')
		expect(report.systemDNS.length).toBeGreaterThan(0)
		expect(report.steps.length).toBeGreaterThan(1)
		expect(Object.values(BlockKind)).toContain(report.kind)
		expect(report.kind).not.toBe(BlockKind.siteDown)
	}, 60_000)

	it('treats a nonexistent name as a failed system lookup', async () => {
		const report = await diagnose('no-such-host-zzz123456.com', { signal: new AbortController().signal })
		expect(report.systemDNS).toEqual([])
	}, 60_000)

	it('returns within milliseconds of a cancel', async () => {
		const controller = new AbortController()
		const pending = diagnose('example.com', { signal: controller.signal })
		const settled = pending.then(() => 'completed', (error: unknown) => error)
		await new Promise((resolve) => setTimeout(resolve, 10))
		const cancelledAt = Date.now()
		controller.abort()
		const outcome = await settled
		if (outcome !== 'completed') expect(outcome).toBeInstanceOf(CancelledError)
		expect(Date.now() - cancelledAt).toBeLessThan(1_000)
	}, 30_000)
})
