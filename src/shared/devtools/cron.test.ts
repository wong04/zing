// Port of the cron checks in Krackn Tests/unit (section F).
import { describe, expect, it } from 'vitest'
import { describeCron } from './cron'

describe('cron (Krackn Tests/unit F)', () => {
	it('cron: valid parses', () => {
		expect(describeCron('0 9 * * 1-5')).not.toBeNull()
	})
	it('cron: wrong arity -> nil', () => {
		expect(describeCron('0 9 *')).toBeNull()
	})
})

// Zing additions.
describe('known answers', () => {
	it('reads each field back', () => {
		expect(describeCron('0 9 * * 1-5')).toBe(
			'Runs at minute 0, hour 9, every day of month, every month, day of week 1-5.',
		)
	})
	it('steps are pluralised properly', () => {
		expect(describeCron('*/15 */2 */3 * *')).toBe(
			'Runs at every 15 minutes, every 2 hours, every 3 days of the month, every month, every day of week.',
		)
	})
	it('extra spaces are not extra fields', () => {
		expect(describeCron('  0  9 * * 1,3  ')).toBe(
			'Runs at minute 0, hour 9, every day of month, every month, day of week 1,3.',
		)
		expect(describeCron('0 9 * * * *')).toBeNull()
	})
})
