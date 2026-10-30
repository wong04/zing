// Port of Krackn's Features/Utilities/Expression.swift: a small
// recursive-descent expression evaluator for the calculator. Parsing it here
// (rather than handing input to `eval`/`Function`) keeps errors as ordinary
// typed throws and makes the whole thing unit-testable.

import { formatFixed, formatG } from './cFormat'

export class EvalError extends Error {
	override name = 'EvalError'
}

export class EmptyExpressionError extends EvalError {
	override name = 'EmptyExpressionError'
	constructor() {
		super('')
	}
}

export class UnexpectedTokenError extends EvalError {
	override name = 'UnexpectedTokenError'
	constructor(readonly token: string) {
		super(`Unexpected '${token}'`)
	}
}

export class UnbalancedParenthesesError extends EvalError {
	override name = 'UnbalancedParenthesesError'
	constructor() {
		super('Unbalanced parentheses')
	}
}

export class UnknownSymbolError extends EvalError {
	override name = 'UnknownSymbolError'
	constructor(readonly symbol: string) {
		super(`Unknown '${symbol}'`)
	}
}

export class BadArgumentError extends EvalError {
	override name = 'BadArgumentError'
	constructor(readonly functionName: string) {
		super(`Invalid input for ${functionName}`)
	}
}

/** Angle handling for the trig functions. */
export const AngleMode = { radians: 'RAD', degrees: 'DEG' } as const

export type AngleMode = (typeof AngleMode)[keyof typeof AngleMode]

export type Variables = Readonly<Record<string, number>>

// MARK: - Public

export function evaluate(input: string, angle: AngleMode = AngleMode.radians, variables: Variables = {}): number {
	return new Parser(input, angle, variables).parse()
}

/**
 * Compiles once, then evaluates repeatedly with a different `x`.
 *
 * Plotting a curve means thousands of evaluations across the viewport. This
 * still re-parses per sample, but hides that behind one entry point so
 * callers can't accidentally re-parse *and* re-normalise per pixel.
 */
export function makeFunction(input: string, angle: AngleMode = AngleMode.radians, variable = 'x'): (x: number) => number {
	return (x) => evaluate(input, angle, { [variable]: x })
}

/**
 * Formats a result the way a calculator should: no trailing `.0`, no
 * exponent for ordinary magnitudes, and full precision when it matters.
 */
export function format(value: number): string {
	if (Number.isNaN(value)) return 'NaN'
	if (!Number.isFinite(value)) return value < 0 ? '-∞' : '∞'
	if (Number.isInteger(value) && Math.abs(value) < 1e16) return formatFixed(value, 0)
	const magnitude = Math.abs(value)
	if (magnitude !== 0 && (magnitude < 1e-4 || magnitude >= 1e12)) return formatG(value)
	// Trim trailing zeros from a fixed representation.
	return formatFixed(value, 10).replace(/0+$/, '').replace(/\.$/, '')
}

// MARK: - Numeric helpers

/** Swift's `rounded()`: to nearest, ties away from zero (`Math.round` sends -2.5 to -2). */
function roundHalfAwayFromZero(x: number): number {
	return Math.sign(x) * Math.round(Math.abs(x))
}

/**
 * C `pow`, which the Swift called. `Math.pow` differs only where IEEE 754 and
 * C99 say `pow(1, y)` and `pow(-1, ±∞)` are 1; JavaScript returns NaN.
 */
function cPow(base: number, exponent: number): number {
	if (base === 1) return 1
	if (base === -1 && (exponent === Infinity || exponent === -Infinity)) return 1
	return Math.pow(base, exponent)
}

function factorial(value: number): number {
	if (value < 0 || value !== roundHalfAwayFromZero(value) || value > 170) {
		throw new BadArgumentError('factorial')
	}
	let result = 1
	for (let n = value; n > 1; n--) result *= n
	return result
}

const letter = /^\p{L}$/u
const digit = /^\p{N}$/u

function isLetter(c: string | undefined): c is string {
	return c !== undefined && letter.test(c)
}

function isNumber(c: string | undefined): c is string {
	return c !== undefined && digit.test(c)
}

const constants: ReadonlyMap<string, number> = new Map([
	['pi', Math.PI],
	['e', Math.E],
	['tau', Math.PI * 2],
	['inf', Infinity],
])

// MARK: - Parser

class Parser {
	private readonly characters: readonly string[]
	private index = 0

	constructor(text: string, private readonly angle: AngleMode, private readonly variables: Variables) {
		// Accept the symbols a calculator keypad produces alongside ASCII.
		const normalised = text
			.replaceAll('×', '*')
			.replaceAll('÷', '/')
			.replaceAll('−', '-')
			.replaceAll('π', 'pi')
			.replaceAll(',', '')
		this.characters = Array.from(normalised)
	}

	parse(): number {
		this.skipSpace()
		if (this.index >= this.characters.length) throw new EmptyExpressionError()
		const value = this.expression()
		this.skipSpace()
		const leftover = this.peek()
		if (leftover !== undefined) throw new UnexpectedTokenError(leftover)
		return value
	}

	// expression := term (('+' | '-') term)*
	private expression(): number {
		let value = this.term()
		for (;;) {
			this.skipSpace()
			const c = this.peek()
			if (c !== '+' && c !== '-') return value
			this.index += 1
			const rhs = this.term()
			value = c === '+' ? value + rhs : value - rhs
		}
	}

	// term := power (('*' | '/' | '%' | implicit) power)*
	private term(): number {
		let value = this.power()
		for (;;) {
			this.skipSpace()
			const c = this.peek()
			if (c === '*' || c === '/' || c === '%') {
				this.index += 1
				const rhs = this.power()
				if (c === '*') value *= rhs
				else if (c === '/') value /= rhs
				else value %= rhs
			} else if (c === '(' || isLetter(c)) {
				// Implicit multiplication: "2(3+4)" and "2pi" behave the way
				// they do on paper.
				value *= this.power()
			} else {
				return value
			}
		}
	}

	// power := unary ('^' power)?   — right associative, so 2^3^2 = 512
	private power(): number {
		const base = this.unary()
		this.skipSpace()
		if (this.peek() !== '^') return base
		this.index += 1
		const exponent = this.power()
		return cPow(base, exponent)
	}

	private unary(): number {
		this.skipSpace()
		const c = this.peek()
		// Negation binds *looser* than exponentiation, so -2^2 is -(2^2),
		// matching every calculator and maths textbook.
		if (c === '-') {
			this.index += 1
			return -this.power()
		}
		if (c === '+') {
			this.index += 1
			return this.power()
		}
		// Prefix root: √16 and √(x+1) both work.
		if (c === '√') {
			this.index += 1
			const value = this.power()
			if (!(value >= 0)) throw new BadArgumentError('sqrt')
			return Math.sqrt(value)
		}
		return this.postfix()
	}

	/** Handles trailing `!` (factorial). */
	private postfix(): number {
		let value = this.primary()
		for (;;) {
			this.skipSpace()
			if (this.peek() !== '!') return value
			this.index += 1
			value = factorial(value)
		}
	}

	private primary(): number {
		this.skipSpace()
		const c = this.peek()
		if (c === undefined) throw new EmptyExpressionError()

		if (c === '(') {
			this.index += 1
			const value = this.expression()
			this.skipSpace()
			if (this.peek() !== ')') throw new UnbalancedParenthesesError()
			this.index += 1
			return value
		}

		if (isNumber(c) || c === '.') return this.number()
		if (isLetter(c)) return this.identifier()
		throw new UnexpectedTokenError(c)
	}

	private takeDigits(): string {
		let text = ''
		for (let c = this.peek(); isNumber(c); c = this.peek()) {
			text += c
			this.index += 1
		}
		return text
	}

	/** Scientific notation: 1.5e-3. Returns '' (and consumes nothing) when the `e` is something else. */
	private exponent(): string {
		const save = this.index
		const marker = this.peek()
		if (marker !== 'e' && marker !== 'E') return ''
		this.index += 1
		let text = marker
		const sign = this.peek()
		if (sign === '+' || sign === '-') {
			text += sign
			this.index += 1
		}
		const digits = this.takeDigits()
		if (digits !== '') return text + digits
		this.index = save // not an exponent after all — probably "e"
		return ''
	}

	private number(): number {
		let text = ''
		for (let c = this.peek(); isNumber(c) || c === '.'; c = this.peek()) {
			text += c
			this.index += 1
		}
		text += this.exponent()
		const value = Number(text)
		if (Number.isNaN(value)) throw new UnexpectedTokenError(text)
		return value
	}

	private lookupVariable(name: string): number | undefined {
		if (Object.hasOwn(this.variables, name)) return this.variables[name]
		const lower = name.toLowerCase()
		if (Object.hasOwn(this.variables, lower)) return this.variables[lower]
		return undefined
	}

	private identifier(): number {
		let name = ''
		for (let c = this.peek(); isLetter(c) || isNumber(c); c = this.peek()) {
			name += c
			this.index += 1
		}
		const lower = name.toLowerCase()

		// A bound variable wins over everything, so `e` can be Euler's
		// number normally but a plot variable if the caller says so.
		const bound = this.lookupVariable(name)
		if (bound !== undefined) return bound
		const constant = constants.get(lower)
		if (constant !== undefined) return constant

		// Everything else must be a function call.
		this.skipSpace()
		if (this.peek() !== '(') throw new UnknownSymbolError(name)
		this.index += 1
		const argument = this.expression()
		this.skipSpace()
		if (this.peek() !== ')') throw new UnbalancedParenthesesError()
		this.index += 1

		return this.apply(lower, argument)
	}

	/** Trig respects the DEG/RAD switch; everything else is unit-free. */
	private toRadians(v: number): number {
		return this.angle === AngleMode.degrees ? v * Math.PI / 180 : v
	}

	private fromRadians(v: number): number {
		return this.angle === AngleMode.degrees ? v * 180 / Math.PI : v
	}

	private apply(name: string, x: number): number {
		switch (name) {
			case 'sin': return Math.sin(this.toRadians(x))
			case 'cos': return Math.cos(this.toRadians(x))
			case 'tan': return Math.tan(this.toRadians(x))
			case 'asin':
				if (!(x >= -1 && x <= 1)) throw new BadArgumentError('asin')
				return this.fromRadians(Math.asin(x))
			case 'acos':
				if (!(x >= -1 && x <= 1)) throw new BadArgumentError('acos')
				return this.fromRadians(Math.acos(x))
			case 'atan': return this.fromRadians(Math.atan(x))
			case 'sinh': return Math.sinh(x)
			case 'cosh': return Math.cosh(x)
			case 'tanh': return Math.tanh(x)
			case 'sqrt':
				if (!(x >= 0)) throw new BadArgumentError('sqrt')
				return Math.sqrt(x)
			case 'cbrt': return Math.cbrt(x)
			case 'ln':
				if (!(x > 0)) throw new BadArgumentError(name)
				return Math.log(x)
			case 'log':
				if (!(x > 0)) throw new BadArgumentError(name)
				return Math.log10(x)
			case 'log2':
				if (!(x > 0)) throw new BadArgumentError('log2')
				return Math.log2(x)
			case 'exp': return Math.exp(x)
			case 'abs': return Math.abs(x)
			case 'round': return roundHalfAwayFromZero(x)
			case 'floor': return Math.floor(x)
			case 'ceil': return Math.ceil(x)
			case 'sign': return x === 0 ? 0 : (x < 0 ? -1 : 1)
			case 'deg': return x * 180 / Math.PI
			case 'rad': return x * Math.PI / 180
			default: throw new UnknownSymbolError(name)
		}
	}

	// MARK: Lexing helpers

	private peek(): string | undefined {
		return this.characters[this.index]
	}

	private skipSpace(): void {
		for (let c = this.peek(); c === ' ' || c === '\t'; c = this.peek()) this.index += 1
	}
}
