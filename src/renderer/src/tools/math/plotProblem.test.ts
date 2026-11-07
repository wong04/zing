import { describe, expect, it } from 'vitest'
import { AngleMode } from '@shared/math/expression'
import { plotProblem } from './plotProblem'

describe('plotProblem', () => {
	it.each([['sqrt(x-2)'], ['ln(x-1)'], ['asin(x+1)']])('%s is undefined at x = 1 but still a curve', (text) => {
		expect(plotProblem(text, AngleMode.radians)).toBeNull()
	})
	it.each([
		['sin(', 'Unbalanced parentheses'],
		['2+', 'Incomplete expression'],
		['foo(x)', "Unknown 'foo'"],
	])('%s is refused: %s', (text, message) => {
		expect(plotProblem(text, AngleMode.radians)).toBe(message)
	})
	it('an empty plot has no problem', () => {
		expect(plotProblem('  ', AngleMode.radians)).toBeNull()
	})
})
