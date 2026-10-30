// Port of the number-base checks in Krackn Tests/unit (section F).
import { describe, expect, it } from 'vitest'
import { convertBase, formatBinaryByteCount } from './numberBase'

describe('number bases (Krackn Tests/unit F)', () => {
	it('base: decimal', () => {
		expect(convertBase('255')?.hex).toBe('0xFF')
	})
	it('base: hex input', () => {
		expect(convertBase('0xFF')?.decimal).toBe('255')
	})
	it('base: binary input', () => {
		expect(convertBase('0b1010')?.decimal).toBe('10')
	})
	it('base: octal input', () => {
		expect(convertBase('0o17')?.decimal).toBe('15')
	})
	it('base: underscores ok', () => {
		expect(convertBase('1_000')?.decimal).toBe('1000')
	})
	it('base: garbage -> nil', () => {
		expect(convertBase('zzz')).toBeNull()
	})
	it('base: empty -> nil', () => {
		expect(convertBase('')).toBeNull()
	})
})

// Zing additions.
describe('known answers', () => {
	it('renders every base', () => {
		expect(convertBase('0b1111_1111')).toEqual({
			decimal: '255',
			hex: '0xFF',
			binary: '0b11111111',
			octal: '0o377',
			bytes: '255 bytes',
		})
	})
	it('is exact across the whole 64-bit range, and refuses beyond it', () => {
		expect(convertBase('0x7FFFFFFFFFFFFFFF')?.decimal).toBe('9223372036854775807')
		expect(convertBase('-9223372036854775808')?.hex).toBe('0x-8000000000000000')
		expect(convertBase('9223372036854775808')).toBeNull()
	})
	it('a prefix with no digits, or digits outside the base, is refused', () => {
		expect(convertBase('0x')).toBeNull()
		expect(convertBase('0b102')).toBeNull()
		expect(convertBase('0o8')).toBeNull()
	})
	it('byte counts read like ByteCountFormatter', () => {
		expect(formatBinaryByteCount(0n)).toBe('Zero KB')
		expect(formatBinaryByteCount(1n)).toBe('1 byte')
		expect(formatBinaryByteCount(1000n)).toBe('1,000 bytes')
		expect(formatBinaryByteCount(4096n)).toBe('4 KB')
		expect(formatBinaryByteCount(1_572_864n)).toBe('1.5 MB')
		expect(formatBinaryByteCount(2n ** 30n)).toBe('1 GB')
	})
})
