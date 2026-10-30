// Port of the units section of Krackn Features/Utilities/DevTools.swift, plus
// the value formatting from the Unit Converter's view (DevToolViews.swift).

export const UnitKind = {
	length: 'Length',
	mass: 'Mass',
	temperature: 'Temperature',
	data: 'Data',
	pressure: 'Pressure',
	energy: 'Energy',
	angle: 'Angle',
} as const
export type UnitKind = (typeof UnitKind)[keyof typeof UnitKind]

export const unitKinds: readonly UnitKind[] = Object.values(UnitKind)

/** Unit name → factor relative to the base unit for that kind. Temperature's factors are placeholders; it converts by offset. */
const unitTable: Readonly<Record<UnitKind, readonly (readonly [string, number])[]>> = {
	[UnitKind.length]: [
		['m', 1],
		['km', 1000],
		['cm', 0.01],
		['mm', 0.001],
		['µm', 1e-6],
		['nm', 1e-9],
		['in', 0.0254],
		['ft', 0.3048],
		['yd', 0.9144],
		['mi', 1609.344],
		['thou', 2.54e-5],
	],
	[UnitKind.mass]: [
		['kg', 1],
		['g', 0.001],
		['mg', 1e-6],
		['t', 1000],
		['lb', 0.45359237],
		['oz', 0.028349523125],
		['st', 6.35029318],
	],
	[UnitKind.temperature]: [
		['°C', 1],
		['K', 1],
		['°F', 1],
	],
	[UnitKind.data]: [
		['B', 1],
		['KB', 1024],
		['MB', 1048576],
		['GB', 1073741824],
		['TB', 1099511627776],
		['bit', 0.125],
	],
	[UnitKind.pressure]: [
		['Pa', 1],
		['kPa', 1000],
		['MPa', 1e6],
		['bar', 100000],
		['psi', 6894.757],
		['atm', 101325],
		['mmHg', 133.322],
	],
	[UnitKind.energy]: [
		['J', 1],
		['kJ', 1000],
		['cal', 4.184],
		['kcal', 4184],
		['Wh', 3600],
		['kWh', 3.6e6],
		['eV', 1.602176634e-19],
		['BTU', 1055.06],
	],
	[UnitKind.angle]: [
		['deg', 1],
		['rad', 57.2957795131],
		['grad', 0.9],
		['turn', 360],
	],
}

const toCelsius: Readonly<Record<string, (value: number) => number>> = {
	'°C': (v) => v,
	K: (v) => v - 273.15,
	'°F': (v) => ((v - 32) * 5) / 9,
}

const fromCelsius: Readonly<Record<string, (celsius: number) => number>> = {
	'°C': (c) => c,
	K: (c) => c + 273.15,
	'°F': (c) => (c * 9) / 5 + 32,
}

/** The unit names for a kind, in display order. */
export function unitsOf(kind: UnitKind): string[] {
	return unitTable[kind].map(([name]) => name)
}

/** Null when either unit isn't one of `kind`'s. */
export function convertUnit(value: number, from: string, to: string, kind: UnitKind): number | null {
	// Temperature is offset-based, so factors don't apply.
	if (kind === UnitKind.temperature) return convertTemperature(value, from, to)
	const table = new Map(unitTable[kind])
	const fromFactor = table.get(from)
	const toFactor = table.get(to)
	if (fromFactor === undefined || toFactor === undefined) return null
	return (value * fromFactor) / toFactor
}

function convertTemperature(value: number, from: string, to: string): number | null {
	const inbound = toCelsius[from]
	const outbound = fromCelsius[to]
	if (!inbound || !outbound) return null
	return outbound(inbound(value))
}

/**
 * A converted value for display, as C's `%g`: six significant digits, no
 * trailing zeros, exponent form for very large or small values. Avoids both
 * "1.0000000001" and losing precision on tiny values.
 */
export function formatUnitValue(value: number): string {
	if (!Number.isFinite(value)) return String(value)
	if (value === 0) return '0'
	const [mantissa = '', exponentText = '0'] = value.toExponential(5).split('e')
	const exponent = Number(exponentText)
	if (exponent < -4 || exponent >= 6) {
		const sign = exponent < 0 ? '-' : '+'
		return `${stripTrailingZeros(mantissa)}e${sign}${String(Math.abs(exponent)).padStart(2, '0')}`
	}
	return stripTrailingZeros(value.toFixed(5 - exponent))
}

function stripTrailingZeros(fixed: string): string {
	return fixed.includes('.') ? fixed.replace(/\.?0+$/, '') : fixed
}
