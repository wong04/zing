import { describe, expect, it } from 'vitest'
import { evaluate, EvalError } from '@shared/math/expression'
import { evalMessage } from './evalMessage'

function messageFor(input: string): string {
	try {
		evaluate(input)
	} catch (error) {
		if (error instanceof EvalError) return evalMessage(error, input)
		throw error
	}
	throw new Error(`${input} evaluated`)
}

describe('evalMessage', () => {
	it('reports an unclosed bracket as unbalanced parentheses', () => {
		expect(messageFor('sin(')).toBe('Unbalanced parentheses')
	})
	it('reports a dangling operator as incomplete', () => {
		expect(messageFor('2+')).toBe('Incomplete expression')
	})
	it('keeps the parser message otherwise', () => {
		expect(messageFor('foo(2)')).toBe("Unknown 'foo'")
	})
})
