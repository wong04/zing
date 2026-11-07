import { describe, expect, it } from 'vitest'
import { CronProblem, nextCronRuns } from './cronNext'

// Local-time constructors throughout: cron fires on the wall clock.
const at = (y: number, mo: number, d: number, h: number, mi: number) => new Date(y, mo - 1, d, h, mi)

describe('next cron runs', () => {
	it('every 15 minutes', () => {
		expect(nextCronRuns('*/15 * * * *', at(2026, 1, 1, 10, 7), 3)).toEqual([at(2026, 1, 1, 10, 15), at(2026, 1, 1, 10, 30), at(2026, 1, 1, 10, 45)])
	})
	it('fields may be split by tabs or several spaces, as in a crontab', () => {
		expect(nextCronRuns('0	9  * *	*', at(2026, 1, 1, 8, 0), 1)).toEqual([at(2026, 1, 1, 9, 0)])
	})
	it('a run at exactly the start time is not the next one', () => {
		expect(nextCronRuns('0 9 * * *', at(2026, 1, 1, 9, 0), 1)).toEqual([at(2026, 1, 2, 9, 0)])
	})
	it('weekday 9am (Mon-Fri) skips the weekend', () => {
		// 2026-01-09 is a Friday.
		expect(nextCronRuns('0 9 * * 1-5', at(2026, 1, 9, 10, 0), 2)).toEqual([at(2026, 1, 12, 9, 0), at(2026, 1, 13, 9, 0)])
	})
	it('lists and ranges', () => {
		expect(nextCronRuns('0,30 8-9 * * *', at(2026, 1, 1, 8, 30), 3)).toEqual([at(2026, 1, 1, 9, 0), at(2026, 1, 1, 9, 30), at(2026, 1, 2, 8, 0)])
	})
	it('day of month and day of week both set: either matches', () => {
		// The 15th (a Thursday in Jan 2026) or any Monday.
		expect(nextCronRuns('0 0 15 * 1', at(2026, 1, 12, 1, 0), 2)).toEqual([at(2026, 1, 15, 0, 0), at(2026, 1, 19, 0, 0)])
	})
	it('Sunday is 0 or 7', () => {
		expect(nextCronRuns('0 0 * * 7', at(2026, 1, 1, 0, 0), 1)).toEqual([at(2026, 1, 4, 0, 0)])
	})
	it('a date that never exists is an error, not an endless search', () => {
		expect(() => nextCronRuns('0 0 31 2 *', at(2026, 1, 1, 0, 0), 1)).toThrow(CronProblem)
	})
	it('a value out of range names the field', () => {
		expect(() => nextCronRuns('61 * * * *', at(2026, 1, 1, 0, 0), 1)).toThrow(/minute/)
	})
	it('a wrong field count is refused', () => {
		expect(() => nextCronRuns('* * *', at(2026, 1, 1, 0, 0), 1)).toThrow(CronProblem)
	})
})
