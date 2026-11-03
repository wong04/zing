import { describe, expect, it } from 'vitest'
import { evaluate, EvalError } from '@shared/math/expression'
import { evalMessage } from './evalMessage'

function messageFor(input: string): string {
	try {
		evaluate(input)
	} catch (error) {
		if (error instanceof EvalError) return evalMessage(error)
		throw error
	}
	throw new Error(`${input} evaluated`)
}

describe('evalMessage', () => {
	it.each([['sin('], ['(2+'], ['2*(3+(4']])('%s is unbalanced', (input) => {
		expect(messageFor(input)).toBe('Unbalanced parentheses')
	})
	it('reports a dangling operator as incomplete', () => {
		expect(messageFor('2+')).toBe('Incomplete expression')
	})
	it.each([['((2+)', "Unexpected ')'"], [')(2+', "Unexpected ')'"], ['foo(2)', "Unknown 'foo'"]])('%s -> %s', (input, message) => {
		expect(messageFor(input)).toBe(message)
	})
})
