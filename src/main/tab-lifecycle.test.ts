import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { defaultAutoClosePolicy, utcCalendar, type AutoClosePolicy } from '@shared/browser/tabLifecycle'
import type { TabRecord } from '@shared/model'
import type { Clock } from '@shared/session/clock'
import { TabAutoCloser, type AutoCloseHost } from './tab-lifecycle'

const HOUR = 3_600_000
const start = Date.UTC(2026, 0, 10, 12, 0, 0)

const clock: Clock = { now: () => Date.now(), onAdvance: () => () => undefined }

function record(id: string, changes: Partial<TabRecord> = {}): TabRecord {
	const at = Date.now()
	return { id, url: `https://${id}.test/`, title: id, pinned: false, createdAt: at, lastActiveAt: at, updatedAt: at, ...changes }
}

class FakeHost implements AutoCloseHost {
	records: TabRecord[]
	activeId: string | undefined
	readonly closed: string[] = []
	closer: TabAutoCloser | undefined

	constructor(records: TabRecord[], activeId: string) {
		this.records = records
		this.activeId = activeId
	}

	close(id: string): void {
		this.closed.push(id)
		this.records = this.records.filter((t) => t.id !== id)
		this.closer?.reschedule()
	}
}

function policyOf(changes: Partial<AutoClosePolicy>): AutoClosePolicy {
	return { ...defaultAutoClosePolicy, enabled: true, ...changes }
}

function arm(host: FakeHost, policy: AutoClosePolicy): TabAutoCloser {
	const closer = new TabAutoCloser(host, () => policy, clock, utcCalendar)
	host.closer = closer
	closer.reschedule()
	return closer
}

beforeEach(() => {
	vi.useFakeTimers()
	vi.setSystemTime(start)
})
afterEach(() => vi.useRealTimers())

describe('TabAutoCloser', () => {
	it('closes an idle tab once its interval passes, sparing active and pinned tabs', () => {
		const host = new FakeHost(
			[record('active'), record('pinned', { pinned: true }), record('idle'), record('busy')],
			'active',
		)
		arm(host, policyOf({ rule: 'hour' }))
		vi.advanceTimersByTime(30 * 60_000)
		host.records = host.records.map((t) => (t.id === 'busy' ? { ...t, lastActiveAt: Date.now() } : t))
		vi.advanceTimersByTime(31 * 60_000)
		expect(host.closed).toEqual(['idle'])
		vi.advanceTimersByTime(30 * 60_000)
		expect(host.closed).toEqual(['idle', 'busy'])
		expect(host.records.map((t) => t.id)).toEqual(['active', 'pinned'])
	})

	it('does nothing while disabled and arms no timer', () => {
		const host = new FakeHost([record('a'), record('b')], 'a')
		arm(host, policyOf({ enabled: false }))
		expect(vi.getTimerCount()).toBe(0)
		vi.advanceTimersByTime(100 * HOUR)
		expect(host.closed).toEqual([])
	})

	it('keeps a single timer armed however often tabs change', () => {
		const host = new FakeHost([record('a'), record('b'), record('c')], 'a')
		const closer = arm(host, policyOf({ rule: 'day' }))
		for (let i = 0; i < 20; i++) closer.reschedule()
		expect(vi.getTimerCount()).toBe(1)
	})

	it('never closes the last tab', () => {
		const host = new FakeHost([record('a'), record('b')], 'a')
		host.activeId = undefined
		arm(host, policyOf({ rule: 'hour' }))
		vi.advanceTimersByTime(2 * HOUR)
		expect(host.records).toHaveLength(1)
	})

	it('sweeps everything but pinned and active at the daily time, regardless of age', () => {
		const host = new FakeHost([record('active'), record('pinned', { pinned: true }), record('x'), record('y')], 'active')
		arm(host, policyOf({ rule: 'week', atTimeEnabled: true, hour: 3, minute: 0 }))
		vi.advanceTimersByTime(14 * HOUR)
		expect(host.closed).toEqual([])
		vi.advanceTimersByTime(HOUR)
		expect(host.closed.sort()).toEqual(['x', 'y'])
		expect(host.records.map((t) => t.id)).toEqual(['active', 'pinned'])
	})

	it('does not fire a daily sweep for a time that passed before the schedule was switched on', () => {
		const host = new FakeHost([record('a'), record('b')], 'a')
		let policy = policyOf({ rule: 'week' })
		const closer = new TabAutoCloser(host, () => policy, clock, utcCalendar)
		host.closer = closer
		closer.reschedule()
		vi.advanceTimersByTime(30 * HOUR)
		policy = policyOf({ rule: 'week', atTimeEnabled: true, hour: 3, minute: 0 })
		closer.reschedule()
		expect(host.closed).toEqual([])
		vi.advanceTimersByTime(25 * HOUR)
		expect(host.closed).toEqual(['b'])
	})
})
