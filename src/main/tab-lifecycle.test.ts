// The scenarios live in tabLifecycle.vectors.json ("autoCloser"), which the C# port's tests play too.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { defaultAutoClosePolicy, utcCalendar, type AutoClosePolicy } from '@shared/browser/tabLifecycle'
import raw from '@shared/browser/tabLifecycle.vectors.json'
import type { TabRecord } from '@shared/model'
import type { Clock } from '@shared/session/clock'
import { TabAutoCloser, type AutoCloseHost } from './tab-lifecycle'

type Step =
	| { advanceMinutes: number }
	| { touch: string }
	| { policy: Partial<AutoClosePolicy> }
	| { reschedule: number }
	| { expectClosed: string[] }
	| { expectRemaining: string[] }
	| { expectRemainingCount: number }
	| { expectTimers: number }

interface Scenario {
	name: string
	policy: Partial<AutoClosePolicy>
	active: string | null
	tabs: { id: string; pinned?: boolean }[]
	steps: Step[]
}

const scenarios = (raw as unknown as { autoCloser: Scenario[] }).autoCloser
const start = Date.parse(raw.scenarioStart)

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

	constructor(records: TabRecord[], activeId: string | undefined) {
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

beforeEach(() => {
	vi.useFakeTimers()
	vi.setSystemTime(start)
})
afterEach(() => vi.useRealTimers())

describe('TabAutoCloser', () => {
	it.each(scenarios)('$name', ({ policy: initial, active, tabs, steps }) => {
		const host = new FakeHost(
			tabs.map((t) => record(t.id, { pinned: t.pinned ?? false })),
			active ?? undefined,
		)
		let policy = policyOf(initial)
		const closer = new TabAutoCloser(host, () => policy, clock, utcCalendar)
		host.closer = closer
		closer.reschedule()

		for (const step of steps) {
			if ('advanceMinutes' in step) vi.advanceTimersByTime(step.advanceMinutes * 60_000)
			else if ('touch' in step) host.records = host.records.map((t) => (t.id === step.touch ? { ...t, lastActiveAt: Date.now() } : t))
			else if ('policy' in step) {
				policy = policyOf(step.policy)
				closer.reschedule()
			} else if ('reschedule' in step) for (let i = 0; i < step.reschedule; i++) closer.reschedule()
			else if ('expectClosed' in step) expect(host.closed).toEqual(step.expectClosed)
			else if ('expectRemaining' in step) expect(host.records.map((t) => t.id)).toEqual(step.expectRemaining)
			else if ('expectRemainingCount' in step) expect(host.records).toHaveLength(step.expectRemainingCount)
			else expect(vi.getTimerCount()).toBe(step.expectTimers)
		}
	})
})
