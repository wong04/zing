import { describe, expect, it } from 'vitest'
import { defaultAutoClosePolicy, utcCalendar, type AutoClosePolicy } from '../browser/tabLifecycle'
import { makeTabSnapshot, type TabSnapshot } from '../browser/tabOrganizer'
import { nextAutoCloseCheck, nextDailyFire } from './autoCloseSchedule'

const HOUR = 3_600_000
const now = Date.UTC(2026, 0, 10, 12, 0, 0)

function policyOf(changes: Partial<AutoClosePolicy>): AutoClosePolicy {
	return { ...defaultAutoClosePolicy, enabled: true, ...changes }
}

function snap(id: string, agoMs: number, changes: Partial<TabSnapshot> = {}): TabSnapshot {
	return { ...makeTabSnapshot({ id, title: id, domain: '', lastAccessed: now - agoMs, createdAt: now - agoMs }), ...changes }
}

describe('nextDailyFire', () => {
	it('is later today when the time has not passed', () => {
		const policy = policyOf({ hour: 15, minute: 30 })
		expect(nextDailyFire(policy, now, utcCalendar)).toBe(Date.UTC(2026, 0, 10, 15, 30))
	})
	it('rolls to tomorrow once it has', () => {
		const policy = policyOf({ hour: 3, minute: 0 })
		expect(nextDailyFire(policy, now, utcCalendar)).toBe(Date.UTC(2026, 0, 11, 3, 0))
	})
	it('does not fire again at the exact instant it just fired', () => {
		const policy = policyOf({ hour: 12, minute: 0 })
		expect(nextDailyFire(policy, now, utcCalendar)).toBe(Date.UTC(2026, 0, 11, 12, 0))
	})
})

describe('nextAutoCloseCheck', () => {
	const tabs = [snap('active', 0, { isActive: true }), snap('old', 2 * HOUR), snap('newer', 10 * 60_000)]

	it('is nothing while disabled', () => {
		expect(nextAutoCloseCheck(tabs, policyOf({ enabled: false }), now)).toBeUndefined()
	})

	it('waits for the earliest-expiring eligible tab, one past its interval', () => {
		const policy = policyOf({ rule: 'day' })
		expect(nextAutoCloseCheck(tabs, policy, now)).toBe(now - 2 * HOUR + 24 * HOUR + 1)
	})

	it('ignores pinned and active tabs', () => {
		const spared = [snap('active', 9 * HOUR, { isActive: true }), snap('pin', 9 * HOUR, { isPinned: true }), snap('x', HOUR)]
		expect(nextAutoCloseCheck(spared, policyOf({ rule: 'day' }), now)).toBe(now - HOUR + 24 * HOUR + 1)
	})

	it('has nothing to wait for with a lone tab', () => {
		expect(nextAutoCloseCheck([snap('only', 5 * HOUR)], policyOf({ rule: 'hour' }), now)).toBeUndefined()
	})

	it('takes the daily time when it comes first', () => {
		const policy = policyOf({ rule: 'week', atTimeEnabled: true, hour: 13, minute: 0 })
		expect(nextAutoCloseCheck(tabs, policy, now, utcCalendar)).toBe(Date.UTC(2026, 0, 10, 13, 0))
	})

	it('schedules the daily time even with a lone tab', () => {
		const policy = policyOf({ atTimeEnabled: true, hour: 13, minute: 0 })
		expect(nextAutoCloseCheck([snap('only', 0)], policy, now, utcCalendar)).toBe(Date.UTC(2026, 0, 10, 13, 0))
	})
})
