// Port of the timestamp checks in Krackn Tests/unit (section C).
import { describe, expect, it } from 'vitest'
import { describeTimestamp, formatRelative } from './timestamp'

const context = { now: new Date('2025-08-01T14:40:00Z'), locale: 'en-US', timeZone: 'UTC' }

describe('timestamps (Krackn Tests/unit C)', () => {
	it('timestamp seconds', () => {
		expect(describeTimestamp('1753800000', context)).toHaveLength(5)
	})
	it('timestamp millis', () => {
		expect(describeTimestamp('1753800000000', context)).toHaveLength(5)
	})
	it('timestamp zero', () => {
		expect(describeTimestamp('0', context)).toHaveLength(5)
	})
	it('timestamp negative', () => {
		expect(describeTimestamp('-100', context)).toHaveLength(5)
	})
	it('timestamp garbage', () => {
		expect(describeTimestamp('abc', context)).toEqual([])
	})
	it('timestamp empty', () => {
		expect(describeTimestamp('', context)).toEqual([])
	})
})

// Zing additions.
describe('known answers', () => {
	it('reads seconds in every notation', () => {
		const rows = new Map(describeTimestamp(' 1753800000 ', context))
		expect(rows.get('ISO 8601 (UTC)')).toBe('2025-07-29T14:40:00Z')
		expect(rows.get('Local time')).toMatch(/^Tuesday, July 29, 2025 at 2:40:00\sPM$/)
		expect(rows.get('Relative')).toBe('3 days ago')
		expect(rows.get('Unix seconds')).toBe('1753800000')
		expect(rows.get('Unix milliseconds')).toBe('1753800000000')
	})
	it('treats a JS-sized number as milliseconds', () => {
		const rows = new Map(describeTimestamp('1753800000500', context))
		expect(rows.get('ISO 8601 (UTC)')).toBe('2025-07-29T14:40:00Z')
		expect(rows.get('Unix seconds')).toBe('1753800000')
		expect(rows.get('Unix milliseconds')).toBe('1753800000500')
	})
	it('the epoch and before it', () => {
		expect(new Map(describeTimestamp('0', context)).get('ISO 8601 (UTC)')).toBe('1970-01-01T00:00:00Z')
		expect(new Map(describeTimestamp('-100', context)).get('ISO 8601 (UTC)')).toBe('1969-12-31T23:58:20Z')
	})
	it('values Swift would have trapped on are refused, not crashed on', () => {
		expect(describeTimestamp('inf', context)).toEqual([])
		expect(describeTimestamp('nan', context)).toEqual([])
		expect(describeTimestamp('1e300', context)).toEqual([])
	})
	it('relative wording picks the largest whole unit', () => {
		const at = (iso: string): string => formatRelative(new Date(iso), context)
		expect(at('2025-08-01T14:40:30Z')).toBe('in 30 seconds')
		expect(at('2025-08-01T12:40:00Z')).toBe('2 hours ago')
		expect(at('2025-08-15T14:40:00Z')).toBe('in 2 weeks')
		expect(at('2023-07-01T00:00:00Z')).toBe('2 years ago')
	})
})
