import { describe, expect, it } from 'vitest'
import { attemptSummary, collapseAttempts, httpsForm, planOpening, strategyLabel, withAttempt } from './openLinkPlan'
import type { RelayAttempt, RelayOpenResult, RelayStrategyName } from './relayOpen'

const result = (strategy: RelayStrategyName, address: string): RelayOpenResult => ({ outcome: 'opened', strategy, address, attempts: [] })

describe('planOpening', () => {
	it('opens an ordinary tab when the direct method worked, whatever the scheme', () => {
		expect(planOpening(result('direct', 'https://a.test/x'))).toEqual({ kind: 'ordinary', url: 'https://a.test/x' })
		expect(planOpening(result('direct', 'http://a.test/x'))).toEqual({ kind: 'ordinary', url: 'http://a.test/x' })
	})

	it.each<RelayStrategyName>(['dohPinned', 'readerRelay', 'wayback'])('opens a relayed tab when %s worked for an https address', (strategy) => {
		expect(planOpening(result(strategy, 'https://a.test/x'))).toEqual({ kind: 'relayed', url: 'https://a.test/x' })
	})

	it('tries the https form of an http address that needed a relay, once', () => {
		const plan = planOpening(result('readerRelay', 'http://a.test:8080/x?q=1'))
		expect(plan).toEqual({ kind: 'retryHttps', url: 'https://a.test:8080/x?q=1' })
		// The retry's own answer is for an https address, so it can't ask for another retry.
		expect(planOpening(result('readerRelay', httpsForm('http://a.test:8080/x?q=1'))).kind).toBe('relayed')
	})
})

describe('withAttempt', () => {
	const event = (strategy: RelayStrategyName, status: RelayAttempt['status'], reason?: string) => ({ runId: 'r', strategy, status, reason })

	it('lists the methods in the order they arrive and updates a method in place', () => {
		let rows: readonly RelayAttempt[] = []
		rows = withAttempt(rows, event('direct', 'trying'))
		rows = withAttempt(rows, event('direct', 'failed', 'no route'))
		rows = withAttempt(rows, event('dohPinned', 'trying'))
		expect(rows.map((r) => `${r.strategy}:${r.status}`)).toEqual(['direct:failed', 'dohPinned:trying'])
		expect(rows[0]?.reason).toBe('no route')
	})
})

describe('collapseAttempts', () => {
	it('keeps one row per method, the last step of each, in order', () => {
		const steps: RelayAttempt[] = [
			{ strategy: 'direct', status: 'trying' },
			{ strategy: 'direct', status: 'failed', reason: 'cut' },
			{ strategy: 'dohPinned', status: 'trying' },
			{ strategy: 'dohPinned', status: 'worked' },
		]
		expect(collapseAttempts(steps).map((r) => `${r.strategy}:${r.status}`)).toEqual(['direct:failed', 'dohPinned:worked'])
	})
})

describe('wording', () => {
	it('names the methods as Diagnose does', () => {
		expect(strategyLabel('dohPinned')).toBe('DoH + IP pinning')
	})

	it('says a failed method did not get through, not that anything broke', () => {
		const text = attemptSummary({ strategy: 'dohPinned', status: 'failed', reason: 'Timed out.' })
		expect(text).toBe("Didn't get through. Timed out.")
		expect(text).not.toMatch(/error|broken|fail/i)
	})
})
