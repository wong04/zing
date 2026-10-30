// Port of `describeTimestamp` from Krackn Features/Utilities/UtilityTools.swift.

/**
 * The clock and locale a description is written against. Passed in, not read,
 * so the functions stay pure; `locale`/`timeZone` default to the system's.
 */
export type DateContext = {
	readonly now: Date
	readonly locale?: string
	readonly timeZone?: string
}

export type TimestampRow = readonly [label: string, value: string]

// Anything past ~2001 in milliseconds is absurd as a seconds value, so treat
// large numbers as ms — that's the usual JS timestamp.
const millisecondsThreshold = 100_000_000_000
const decimalNumber = /^[+-]?(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?$/

const relativeUnits: readonly [Intl.RelativeTimeFormatUnit, number][] = [
	['year', 365 * 86_400],
	['month', 30 * 86_400],
	['week', 7 * 86_400],
	['day', 86_400],
	['hour', 3_600],
	['minute', 60],
]

/** Every useful reading of a Unix timestamp (seconds or milliseconds). Empty when it isn't one. */
export function describeTimestamp(raw: string, context: DateContext): TimestampRow[] {
	const trimmed = raw.replace(/^[\t\p{Zs}]+|[\t\p{Zs}]+$/gu, '')
	if (!decimalNumber.test(trimmed)) return []
	const value = Number(trimmed)
	const seconds = value > millisecondsThreshold ? value / 1000 : value
	const date = new Date(seconds * 1000)
	if (Number.isNaN(date.getTime())) return []

	return [
		['ISO 8601 (UTC)', isoInternetDateTime(date)],
		['Local time', formatFullDateTime(date, context)],
		['Relative', formatRelative(date, context)],
		['Unix seconds', String(Math.trunc(seconds))],
		['Unix milliseconds', String(Math.trunc(seconds * 1000))],
	]
}

/** `2025-07-29T14:40:00Z`: whole seconds, no fraction, like ISO8601DateFormatter's internet date-time. */
export function isoInternetDateTime(date: Date): string {
	return date.toISOString().replace(/\.\d{3}Z$/, 'Z')
}

export function formatFullDateTime(date: Date, context: DateContext): string {
	return new Intl.DateTimeFormat(context.locale, {
		dateStyle: 'full',
		timeStyle: 'medium',
		timeZone: context.timeZone,
	}).format(date)
}

export function formatMediumDateTime(date: Date, context: DateContext): string {
	return new Intl.DateTimeFormat(context.locale, {
		dateStyle: 'medium',
		timeStyle: 'medium',
		timeZone: context.timeZone,
	}).format(date)
}

/** "in 3 days", "2 months ago": the largest whole unit, as RelativeDateTimeFormatter's full style reads. */
export function formatRelative(date: Date, context: DateContext): string {
	const deltaSeconds = (date.getTime() - context.now.getTime()) / 1000
	const format = new Intl.RelativeTimeFormat(context.locale, { numeric: 'always', style: 'long' })
	for (const [unit, size] of relativeUnits) {
		if (Math.abs(deltaSeconds) >= size) return format.format(Math.trunc(deltaSeconds / size), unit)
	}
	return format.format(Math.trunc(deltaSeconds), 'second')
}
