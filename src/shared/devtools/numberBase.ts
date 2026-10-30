// Port of `convertBase` from Krackn Features/Utilities/DevTools.swift.
//
// Values are 64-bit signed integers, as Swift's `Int` was: BigInt keeps every
// digit exact past 2^53.

export type BaseConversion = {
	readonly decimal: string
	readonly hex: string
	readonly binary: string
	readonly octal: string
	readonly bytes: string
}

const int64Min = -(2n ** 63n)
const int64Max = 2n ** 63n - 1n

const prefixed: readonly { prefix: string; radix: number; digits: RegExp }[] = [
	{ prefix: '0x', radix: 16, digits: /^[+-]?[0-9a-f]+$/ },
	{ prefix: '0b', radix: 2, digits: /^[+-]?[01]+$/ },
	{ prefix: '0o', radix: 8, digits: /^[+-]?[0-7]+$/ },
]
const decimalDigits = /^[+-]?[0-9]+$/

const byteUnits: readonly { unit: string; fractionDigits: number }[] = [
	{ unit: 'KB', fractionDigits: 0 },
	{ unit: 'MB', fractionDigits: 1 },
	{ unit: 'GB', fractionDigits: 2 },
	{ unit: 'TB', fractionDigits: 2 },
	{ unit: 'PB', fractionDigits: 2 },
	{ unit: 'EB', fractionDigits: 2 },
]

/** Parses a value written in any common base (0x…, 0b…, 0o…, or decimal) and renders it in all of them. */
export function convertBase(input: string): BaseConversion | null {
	const text = input.trim().replaceAll('_', '').replaceAll(' ', '')
	if (text === '') return null
	const value = parseInteger(text.toLowerCase())
	if (value === null || value < int64Min || value > int64Max) return null
	return {
		decimal: value.toString(10),
		hex: '0x' + value.toString(16).toUpperCase(),
		binary: '0b' + value.toString(2),
		octal: '0o' + value.toString(8),
		bytes: formatBinaryByteCount(value),
	}
}

function parseInteger(lower: string): bigint | null {
	const base = prefixed.find((p) => lower.startsWith(p.prefix))
	if (!base) return decimalDigits.test(lower) ? BigInt(lower) : null
	const digits = lower.slice(base.prefix.length)
	if (!base.digits.test(digits)) return null
	const negative = digits.startsWith('-')
	const magnitude = digits.replace(/^[+-]/, '')
	const value = [...magnitude].reduce((sum, d) => sum * BigInt(base.radix) + BigInt(parseInt(d, base.radix)), 0n)
	return negative ? -value : value
}

/**
 * A byte count the way ByteCountFormatter's binary style writes it: "Zero KB",
 * "1 byte", "512 bytes", "4 KB", "1.5 MB", "2.25 GB" — powers of 1024 but
 * labelled KB/MB, more decimals for larger units, trailing zeros dropped.
 */
export function formatBinaryByteCount(count: bigint): string {
	if (count === 0n) return 'Zero KB'
	const magnitude = count < 0n ? -count : count
	if (magnitude === 1n) return `${count} byte`
	if (magnitude < 1024n) return `${grouped(Number(count), 0)} bytes`
	let scaled = Number(count) / 1024
	let index = 0
	while (Math.abs(scaled) >= 1024 && index < byteUnits.length - 1) {
		scaled /= 1024
		index += 1
	}
	const { unit, fractionDigits } = byteUnits[index] as (typeof byteUnits)[number]
	return `${grouped(scaled, fractionDigits)} ${unit}`
}

function grouped(value: number, maximumFractionDigits: number): string {
	return new Intl.NumberFormat('en-US', { maximumFractionDigits }).format(value)
}
