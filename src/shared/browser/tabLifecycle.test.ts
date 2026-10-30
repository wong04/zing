// Port of Krackn `Tests/taborg`: auto-close, schedule window, interval labels.
import { describe, expect, it } from 'vitest'
import {
	defaultAutoClosePolicy,
	describedInterval,
	scheduleElapsed,
	tabsToClose,
	tabsToCloseOnSchedule,
	utcCalendar,
	type AutoClosePolicy,
} from './tabLifecycle'
import type { TabSnapshot } from './tabOrganizer'
import { base, tab } from './taborgFixtures'

const now = base

function policyOf(changes: Partial<AutoClosePolicy>): AutoClosePolicy {
	return { ...defaultAutoClosePolicy, ...changes }
}

function closing(tabs: TabSnapshot[], policy: AutoClosePolicy): string[] {
	return tabsToClose(tabs, policy, now).sort()
}

describe('auto-close', () => {
	const aged = [
		tab('fresh', 'a.com', { accessedMinutesAgo: 5 }), // 5 minutes
		tab('stale', 'b.com', { accessedMinutesAgo: 120 }), // 2 hours
		tab('ancient', 'c.com', { accessedMinutesAgo: 20_000 }), // ~14 days
	]

	it('1 hour closes anything older', () => {
		expect(closing(aged, policyOf({ enabled: true, rule: 'hour' }))).toEqual(['ancient', 'stale'])
	})
	it('1 day spares the 2-hour-old tab', () => {
		expect(closing(aged, policyOf({ enabled: true, rule: 'day' }))).toEqual(['ancient'])
	})
	it('7 days spares all but the 14-day-old', () => {
		expect(closing(aged, policyOf({ enabled: true, rule: 'week' }))).toEqual(['ancient'])
	})
	it('custom 1.5h', () => {
		expect(closing(aged, policyOf({ enabled: true, rule: 'custom', customHours: 1.5 }))).toEqual(['ancient', 'stale'])
	})
	it('disabled closes nothing', () => {
		expect(closing(aged, policyOf({ enabled: false, rule: 'custom', customHours: 1.5 }))).toEqual([])
	})

	// The three exemptions, each for a different reason.
	const hourly = policyOf({ enabled: true, rule: 'hour' })
	const withPinned = [
		tab('pinned', 'a.com', { accessedMinutesAgo: 5000, pinned: true }),
		tab('loose', 'b.com', { accessedMinutesAgo: 5000 }),
		tab('loose2', 'c.com', { accessedMinutesAgo: 5000 }),
	]

	it('never closes the active tab', () => {
		const withActive = [
			tab('active', 'a.com', { accessedMinutesAgo: 5000, active: true }),
			tab('other', 'b.com', { accessedMinutesAgo: 5000 }),
			tab('third', 'c.com', { accessedMinutesAgo: 5000 }),
		]
		expect(closing(withActive, hourly)).toEqual(['other', 'third'])
	})
	it('never closes a pinned tab', () => {
		expect(closing(withPinned, hourly)).toEqual(['loose', 'loose2'])
	})
	it('never closes the last tab', () => {
		expect(closing([tab('solo', 'a.com', { accessedMinutesAgo: 99999 })], hourly)).toEqual([])
	})
	it('leaves one survivor from two stale tabs', () => {
		const two = [tab('one', 'a.com', { accessedMinutesAgo: 99999 }), tab('two', 'b.com', { accessedMinutesAgo: 99999 })]
		expect(closing(two, hourly)).toHaveLength(1)
	})
	// The scheduled sweep ignores age but keeps the same exemptions.
	it('scheduled sweep spares pinned', () => {
		expect(tabsToCloseOnSchedule(withPinned).sort()).toEqual(['loose', 'loose2'])
	})
})

describe('schedule window', () => {
	const at = (y: number, m: number, d: number, h: number, min: number) => Date.UTC(y, m - 1, d, h, min)
	const timed = policyOf({ enabled: true, rule: 'day', atTimeEnabled: true, hour: 3, minute: 0 })
	const elapsed = (policy: AutoClosePolicy, since: number, until: number) =>
		scheduleElapsed(policy, since, until, utcCalendar)

	it('fires when 03:00 falls inside the window', () => {
		expect(elapsed(timed, at(2026, 3, 1, 2, 55), at(2026, 3, 1, 3, 5))).toBe(true)
	})
	it('doesn\'t fire before the time', () => {
		expect(elapsed(timed, at(2026, 3, 1, 1, 0), at(2026, 3, 1, 2, 0))).toBe(false)
	})
	it('doesn\'t fire after it already passed', () => {
		expect(elapsed(timed, at(2026, 3, 1, 4, 0), at(2026, 3, 1, 5, 0))).toBe(false)
	})
	// The reason it's an interval test and not "is it 03:00 now": a machine asleep
	// across the instant must still sweep when it wakes.
	it('fires after sleeping across the instant', () => {
		expect(elapsed(timed, at(2026, 3, 1, 22, 0), at(2026, 3, 2, 9, 0))).toBe(true)
	})
	it('a multi-day gap still fires exactly once', () => {
		expect(elapsed(timed, at(2026, 3, 1, 4, 0), at(2026, 3, 5, 4, 0))).toBe(true)
	})
	it('no schedule when the option is off', () => {
		expect(elapsed({ ...timed, atTimeEnabled: false }, at(2026, 3, 1, 2, 0), at(2026, 3, 1, 4, 0))).toBe(false)
	})
})

describe('interval labels', () => {
	const label = (hours: number) => describedInterval(policyOf({ enabled: true, rule: 'custom', customHours: hours }))

	it.each([
		[0.5, '30 minutes'],
		[1, '1 hour'],
		[6, '6 hours'],
		[24, '1 day'],
		[72, '3 days'],
	])('%s hours -> %s', (hours, want) => {
		expect(label(hours)).toBe(want)
	})
})
