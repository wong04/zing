// Port of Krackn's Tests/calc/main.swift (61 assertions).

import { describe, expect, it } from 'vitest'
import {
	AngleMode,
	BadArgumentError,
	EmptyExpressionError,
	EvalError,
	evaluate,
	format,
	UnbalancedParenthesesError,
	UnexpectedTokenError,
	UnknownSymbolError,
} from './expression'

/** Krackn's `eq`: within `tol`, or within 1e-12 relative error. */
function eq(expr: string, want: number, tol = 1e-9, angle: AngleMode = AngleMode.radians): void {
	const got = evaluate(expr, angle)
	const close = Math.abs(got - want) <= tol || (want !== 0 && Math.abs((got - want) / want) <= 1e-12)
	expect(close, `${expr} = ${got}, want ${want}`).toBe(true)
}

function eqDegrees(expr: string, want: number): void {
	eq(expr, want, 1e-9, AngleMode.degrees)
}

describe('arithmetic & precedence', () => {
	it.each([
		['1+2', 3], ['2*3+4', 10], ['4+2*3', 10], ['(4+2)*3', 18],
		['10/4', 2.5], ['7%3', 1], ['-5+2', -3], ['--5', 5], ['+-3', -3],
		['2^3', 8], ['2^3^2', 512], // right associative
		['-2^2', -4], // unary binds looser than ^
		['1.5e3', 1500], ['2e-3', 0.002],
	])('%s = %s', (expr, want) => eq(expr, want))
})

describe('implicit multiplication', () => {
	it.each([
		['2(3+4)', 14], ['(1+1)(2+2)', 8], ['3pi', 3 * Math.PI],
	])('%s = %s', (expr, want) => eq(expr, want))
})

describe('functions', () => {
	it.each([
		['sqrt(16)', 4], ['cbrt(27)', 3], ['abs(-7)', 7],
		['ln(e)', 1], ['log(1000)', 3], ['log2(8)', 3], ['exp(0)', 1],
		['round(2.5)', 3], ['floor(2.9)', 2], ['ceil(2.1)', 3], ['sign(-9)', -1],
		['sin(0)', 0], ['cos(0)', 1],
		['deg(pi)', 180], ['rad(180)', Math.PI],
	])('%s = %s', (expr, want) => eq(expr, want))

	it.each([
		['sin(90)', 1], ['cos(180)', -1],
		['tan(45)', 1],
		['asin(1)', 90],
	])('%s = %s in degrees', (expr, want) => eqDegrees(expr, want))
})

describe('factorial & constants', () => {
	it.each([
		['5!', 120], ['0!', 1], ['3!+2', 8],
		['pi', Math.PI], ['tau', 2 * Math.PI],
	])('%s = %s', (expr, want) => eq(expr, want))
})

describe('keypad symbols', () => {
	it.each([
		['6×7', 42], ['9÷3', 3], ['√16', 4], ['1,000+1', 1001],
	])('%s = %s', (expr, want) => eq(expr, want))
})

describe('errors are thrown, not crashes', () => {
	it.each([
		'', '2+', '(1+2', '1+2)', 'foo(2)', 'sqrt(-1)',
		'ln(0)', 'asin(5)', '2 3 &',
	])("'%s' throws", (expr) => {
		// Krackn accepts any throw; an EvalError also proves it wasn't a crash in disguise.
		expect(() => evaluate(expr)).toThrow(EvalError)
	})
})

describe('formatting', () => {
	it.each([
		[3, '3'], [2.5, '2.5'], [-0.125, '-0.125'], [1 / 3, '0.3333333333'],
		[1e15, '1000000000000000'], [NaN, 'NaN'], [Infinity, '∞'],
	])('format(%s) = %s', (value, want) => {
		expect(format(value)).toBe(want)
	})
})

// Not in Krackn: places where JavaScript's Math differs from the Swift/C the
// calculator was written against, and the specific errors callers can show.
describe('Swift numeric semantics', () => {
	it('round sends ties away from zero on both sides', () => {
		expect(evaluate('round(-2.5)')).toBe(-3)
	})
	it('1 to any power is 1, as C pow says', () => {
		expect(evaluate('1^inf')).toBe(1)
	})
	it('format keeps printf\'s negative zero', () => {
		expect(format(-0)).toBe('-0')
	})
	it('switches to exponent form for tiny and huge non-integers', () => {
		expect(format(0.00001234)).toBe('1.234e-05')
		expect(format(1.5e20)).toBe('1.5e+20')
	})
})

describe('error types', () => {
	it.each([
		['', EmptyExpressionError],
		['1+2)', UnexpectedTokenError],
		['(1+2', UnbalancedParenthesesError],
		['foo(2)', UnknownSymbolError],
		['sqrt(-1)', BadArgumentError],
		['2.5!', BadArgumentError],
	] as const)("'%s' throws %o", (expr, errorClass) => {
		expect(() => evaluate(expr)).toThrow(errorClass)
	})
	it('a variable named like an Object method is not picked up from the prototype', () => {
		expect(() => evaluate('constructor')).toThrow(UnknownSymbolError)
	})
})
