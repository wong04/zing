// Port of Krackn `Tests/taborg`: auto-close, schedule window, interval labels.
// The cases live in tabLifecycle.vectors.json, which the C# port's tests read too.
import { describe, expect, it } from 'vitest'
import {
	autoCloseInterval,
	defaultAutoClosePolicy,
	describedInterval,
	scheduleElapsed,
	tabsToClose,
	tabsToCloseOnSchedule,
	utcCalendar,
	type AutoClosePolicy,
} from './tabLifecycle'
import { keepAwakeReason, nextSleepCheck, tabsToSleep, type SleepCandidate, type SleepSettings } from '../layout/sleep'
import type { TabSnapshot } from './tabOrganizer'
import { base, tab } from './taborgFixtures'
import raw from './tabLifecycle.vectors.json'

interface TabSpec {
	id: string
	minutesAgo: number
	pinned?: boolean
	active?: boolean
}

interface SleepTabSpec extends Partial<SleepCandidate> {
	lastActiveAgoMinutes?: number
}

interface Vectors {
	now: number
	autoClose: { name: string; policy: Partial<AutoClosePolicy>; tabs: string | TabSpec[]; expect?: string[]; expectCount?: number }[]
	tabSets: Record<string, TabSpec[]>
	scheduledSweep: { name: string; tabs: string; expect: string[] }[]
	scheduleElapsed: {
		policy: AutoClosePolicy
		cases: { name: string; policyChanges?: Partial<AutoClosePolicy>; since: string; until: string; expect: boolean }[]
	}
	intervalLabels: { hours: number; expect: string }[]
	intervals: { name: string; policy: Partial<AutoClosePolicy>; expectMs: number }[]
	keepAwake: {
		settings: SleepSettings
		base: SleepTabSpec
		cases: { name: string; tab?: SleepTabSpec; settingsChanges?: Partial<SleepSettings>; expect: string | null }[]
		tabsToSleep: { tabs: SleepTabSpec[]; expect: string[] }
		nextSleepCheck: { name: string; tabs: SleepTabSpec[]; settingsChanges?: Partial<SleepSettings>; expectInMinutes: number | null }[]
	}
}

const vectors = raw as unknown as Vectors
const now = base
const MINUTE = 60_000

function policyOf(changes: Partial<AutoClosePolicy>): AutoClosePolicy {
	return { ...defaultAutoClosePolicy, ...changes }
}

function snapshots(specs: string | TabSpec[]): TabSnapshot[] {
	const list = typeof specs === 'string' ? vectors.tabSets[specs]! : specs
	return list.map((t) => tab(t.id, 'x.com', { accessedMinutesAgo: t.minutesAgo, pinned: t.pinned, active: t.active }))
}

function candidate(spec: SleepTabSpec): SleepCandidate {
	const { lastActiveAgoMinutes, ...rest } = { ...vectors.keepAwake.base, ...spec }
	return {
		id: 't',
		pinned: false,
		isActive: false,
		visibleInSplit: false,
		playingAudio: false,
		hasUnsavedForm: false,
		asleep: false,
		...rest,
		lastActiveAt: now - (lastActiveAgoMinutes ?? 0) * MINUTE,
	}
}

describe('vectors', () => {
	it('share the clock the fixtures use', () => {
		expect(vectors.now).toBe(now)
	})
})

describe('auto-close', () => {
	it.each(vectors.autoClose)('$name', ({ policy, tabs, expect: want, expectCount }) => {
		const closing = tabsToClose(snapshots(tabs), policyOf(policy), now).sort()
		if (want) expect(closing).toEqual(want)
		if (expectCount !== undefined) expect(closing).toHaveLength(expectCount)
	})

	// The scheduled sweep ignores age but keeps the same exemptions.
	it.each(vectors.scheduledSweep)('scheduled sweep: $name', ({ tabs, expect: want }) => {
		expect(tabsToCloseOnSchedule(snapshots(tabs)).sort()).toEqual(want)
	})
})

describe('schedule window', () => {
	const { policy, cases } = vectors.scheduleElapsed
	it.each(cases)('$name', ({ policyChanges, since, until, expect: want }) => {
		const result = scheduleElapsed({ ...policy, ...policyChanges }, Date.parse(since), Date.parse(until), utcCalendar)
		expect(result).toBe(want)
	})
})

describe('intervals', () => {
	it.each(vectors.intervalLabels)('$hours hours -> $expect', ({ hours, expect: want }) => {
		expect(describedInterval(policyOf({ enabled: true, rule: 'custom', customHours: hours }))).toBe(want)
	})
	it.each(vectors.intervals)('$name', ({ policy, expectMs }) => {
		expect(autoCloseInterval(policyOf(policy))).toBe(expectMs)
	})
})

describe('keepAwakeReason', () => {
	const { settings } = vectors.keepAwake
	it.each(vectors.keepAwake.cases)('$name', ({ tab: changes, settingsChanges, expect: want }) => {
		const reason = keepAwakeReason(candidate(changes ?? {}), now, { ...settings, ...settingsChanges })
		expect(reason ?? null).toBe(want)
	})

	it('tabsToSleep picks the idle, unprotected tabs', () => {
		const { tabs, expect: want } = vectors.keepAwake.tabsToSleep
		expect(tabsToSleep(tabs.map(candidate), now, settings)).toEqual(want)
	})

	it.each(vectors.keepAwake.nextSleepCheck)('nextSleepCheck: $name', ({ tabs, settingsChanges, expectInMinutes }) => {
		const next = nextSleepCheck(tabs.map(candidate), now, { ...settings, ...settingsChanges })
		expect(next === undefined ? null : (next - now) / MINUTE).toBe(expectInMinutes)
	})
})
