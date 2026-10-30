import { DEFAULT_SLEEP_SETTINGS, type SleepSettings } from '../layout/sleep'

export const DEFAULT_SLEEPING_TABS: SleepSettings = Object.freeze({ ...DEFAULT_SLEEP_SETTINGS })

export const MIN_SLEEP_MINUTES = 1
export const MAX_SLEEP_MINUTES = 7 * 24 * 60

/** The `sleepingTabs` setting: exactly these three keys, with a sane inactivity window. */
export function isSleepSettings(value: unknown): value is SleepSettings {
	if (typeof value !== 'object' || value === null || Array.isArray(value)) return false
	const keys = Object.keys(value)
	if (keys.length !== 3) return false
	const { enabled, afterMinutes, sleepPinned } = value as Record<string, unknown>
	return (
		typeof enabled === 'boolean' &&
		typeof sleepPinned === 'boolean' &&
		typeof afterMinutes === 'number' &&
		Number.isFinite(afterMinutes) &&
		afterMinutes >= MIN_SLEEP_MINUTES &&
		afterMinutes <= MAX_SLEEP_MINUTES
	)
}
