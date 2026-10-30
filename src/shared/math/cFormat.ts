// Krackn formats numbers with `String(format:)`, i.e. C printf. These
// reproduce the two conversions it uses, so labels and results read the same.

/** printf `%.Nf`. `toFixed` already matches it except for negative zero. */
export function formatFixed(value: number, decimals: number): string {
	if (Object.is(value, -0)) return '-' + (0).toFixed(decimals)
	return value.toFixed(decimals)
}

function trimFraction(text: string): string {
	if (!text.includes('.')) return text
	return text.replace(/0+$/, '').replace(/\.$/, '')
}

/** printf `%g`: six significant digits, exponent form outside 1e-4 ..< 1e6, no trailing zeros. */
export function formatG(value: number): string {
	if (Number.isNaN(value)) return 'nan'
	if (!Number.isFinite(value)) return value < 0 ? '-inf' : 'inf'
	if (value === 0) return formatFixed(value, 0)

	const precision = 6
	const [mantissa = '', exponentText = '0'] = value.toExponential(precision - 1).split('e')
	const exponent = Number(exponentText)
	if (exponent < -4 || exponent >= precision) {
		const sign = exponent < 0 ? '-' : '+'
		return `${trimFraction(mantissa)}e${sign}${String(Math.abs(exponent)).padStart(2, '0')}`
	}
	return trimFraction(value.toFixed(precision - 1 - exponent))
}
