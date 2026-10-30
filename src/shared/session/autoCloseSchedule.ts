import { autoCloseInterval, localCalendar, type AutoClosePolicy, type DayCalendar } from '../browser/tabLifecycle'
import type { TabSnapshot } from '../browser/tabOrganizer'

/** Next instant of `hour:minute` strictly after `now`. */
export function nextDailyFire(policy: AutoClosePolicy, now: number, calendar: DayCalendar = localCalendar): number {
	const today = calendar.startOfDay(now)
	const fire = calendar.atTime(today, policy.hour, policy.minute)
	if (fire > now) return fire
	return calendar.atTime(calendar.startOfDay(calendar.addDays(today, 1)), policy.hour, policy.minute)
}

/**
 * The soonest moment a sweep could do something, so one timer replaces polling.
 * `tabsToClose` closes a tab once its age is strictly past the interval, hence the +1.
 */
export function nextAutoCloseCheck(
	tabs: readonly TabSnapshot[],
	policy: AutoClosePolicy,
	now: number,
	calendar: DayCalendar = localCalendar,
): number | undefined {
	if (!policy.enabled) return undefined
	const targets: number[] = []
	if (policy.atTimeEnabled) targets.push(nextDailyFire(policy, now, calendar))
	// A lone tab is never closed, so waiting on its age would only spin.
	if (tabs.length > 1) {
		const interval = autoCloseInterval(policy)
		for (const tab of tabs) {
			if (!tab.isPinned && !tab.isActive) targets.push(tab.lastAccessed + interval + 1)
		}
	}
	return targets.length === 0 ? undefined : Math.min(...targets)
}
