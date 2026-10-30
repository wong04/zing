// Port of Krackn's `AutoCloseRule`, `AutoClosePolicy` and `TabLifecycle`
// (Features/Browser/TabOrganizer.swift). Times are epoch milliseconds.
import type { TabSnapshot } from './tabOrganizer'

const hourMs = 3_600_000

/** How long a tab may sit untouched before it's closed. */
export const autoCloseRules = ['hour', 'day', 'week', 'custom'] as const
export type AutoCloseRule = (typeof autoCloseRules)[number]

export const autoCloseRuleLabel: Readonly<Record<AutoCloseRule, string>> = {
	hour: '1 hour',
	day: '1 day',
	week: '7 days',
	custom: 'Custom',
}

/** Milliseconds of inactivity, or null for `custom` where the user sets it. */
export const autoCloseRuleInterval: Readonly<Record<AutoCloseRule, number | null>> = {
	hour: hourMs,
	day: 24 * hourMs,
	week: 7 * 24 * hourMs,
	custom: null,
}

export interface AutoClosePolicy {
	enabled: boolean
	rule: AutoCloseRule
	/** Used only when `rule` is `custom`. */
	customHours: number
	/** Independently of inactivity, sweep at a fixed time each day. */
	atTimeEnabled: boolean
	hour: number
	minute: number
}

export const defaultAutoClosePolicy: Readonly<AutoClosePolicy> = {
	enabled: false,
	rule: 'day',
	customHours: 12,
	atTimeEnabled: false,
	hour: 3,
	minute: 0,
}

function customHours(policy: AutoClosePolicy): number {
	return Math.max(policy.customHours, 0.25)
}

export function autoCloseInterval(policy: AutoClosePolicy): number {
	return autoCloseRuleInterval[policy.rule] ?? customHours(policy) * hourMs
}

export function describedInterval(policy: AutoClosePolicy): string {
	if (policy.rule !== 'custom') return autoCloseRuleLabel[policy.rule]
	const hours = customHours(policy)
	if (hours < 1) return `${Math.trunc(hours * 60)} minutes`
	if (hours === 1) return '1 hour'
	if (hours % 24 === 0) {
		const days = Math.trunc(hours / 24)
		return days === 1 ? '1 day' : `${days} days`
	}
	return `${Math.trunc(hours)} hours`
}

/**
 * Pinned tabs are spared because being spared is what pinning means, the
 * active tab because closing what someone is looking at is indefensible, and
 * the final tab because a browser with no tabs is a broken browser.
 */
function closable(tabs: readonly TabSnapshot[], isEligible: (tab: TabSnapshot) => boolean): string[] {
	const doomed: string[] = []
	let survivors = tabs.length
	for (const tab of tabs) {
		if (survivors <= 1) break
		if (tab.isPinned || tab.isActive || !isEligible(tab)) continue
		doomed.push(tab.id)
		survivors -= 1
	}
	return doomed
}

/** Which tabs the sweeper should close. */
export function tabsToClose(tabs: readonly TabSnapshot[], policy: AutoClosePolicy, now: number): string[] {
	if (!policy.enabled) return []
	const cutoff = now - autoCloseInterval(policy)
	return closable(tabs, (tab) => tab.lastAccessed < cutoff)
}

/**
 * Everything eligible for the scheduled daily sweep — same exemptions,
 * but age doesn't matter because the user asked for a clean slate at a
 * particular time.
 */
export function tabsToCloseOnSchedule(tabs: readonly TabSnapshot[]): string[] {
	return closable(tabs, () => true)
}

/** The calendar arithmetic `scheduleElapsed` needs, so tests can pin a time zone. */
export interface DayCalendar {
	startOfDay(time: number): number
	/** The instant at `hour:minute:00` on the day that starts at `dayStart`. */
	atTime(dayStart: number, hour: number, minute: number): number
	addDays(time: number, days: number): number
}

export const localCalendar: DayCalendar = {
	startOfDay: (time) => new Date(time).setHours(0, 0, 0, 0),
	atTime: (dayStart, hour, minute) => new Date(dayStart).setHours(hour, minute, 0, 0),
	addDays: (time, days) => {
		const date = new Date(time)
		date.setDate(date.getDate() + days)
		return date.getTime()
	},
}

export const utcCalendar: DayCalendar = {
	startOfDay: (time) => new Date(time).setUTCHours(0, 0, 0, 0),
	atTime: (dayStart, hour, minute) => new Date(dayStart).setUTCHours(hour, minute, 0, 0),
	addDays: (time, days) => {
		const date = new Date(time)
		date.setUTCDate(date.getUTCDate() + days)
		return date.getTime()
	},
}

/**
 * Whether the daily sweep instant fell between two checks.
 *
 * Comparing against "is it 03:00 right now" would miss the sweep whenever
 * the app happened not to tick on that exact minute — asleep, busy, or
 * simply between timer fires. Asking whether the scheduled instant lies in
 * the interval since the last check can't miss it.
 */
export function scheduleElapsed(
	policy: AutoClosePolicy,
	since: number,
	now: number,
	calendar: DayCalendar = localCalendar,
): boolean {
	if (!policy.enabled || !policy.atTimeEnabled || now <= since) return false
	// Walk each day the interval spans and check that day's scheduled
	// instant. An interval longer than a day therefore still fires once.
	for (let probe = calendar.startOfDay(since); probe <= now; probe = calendar.addDays(probe, 1)) {
		const fire = calendar.atTime(probe, policy.hour, policy.minute)
		if (fire > since && fire <= now) return true
	}
	return false
}
