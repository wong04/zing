// Port of the unit-conversion checks in Krackn Tests/unit (section F).
import { describe, expect, it } from 'vitest'
import { convertUnit, formatUnitValue, UnitKind, unitKinds, unitsOf } from './units'

function near(value: number | null, want: number): boolean {
	return Math.abs((value ?? 0) - want) < 0.001
}

describe('units (Krackn Tests/unit F)', () => {
	it('unit: m to ft', () => {
		expect(near(convertUnit(1, 'm', 'ft', UnitKind.length), 3.28084)).toBe(true)
	})
	it('unit: C to F', () => {
		expect(near(convertUnit(100, '°C', '°F', UnitKind.temperature), 212)).toBe(true)
	})
	it('unit: C to K', () => {
		expect(near(convertUnit(0, '°C', 'K', UnitKind.temperature), 273.15)).toBe(true)
	})
	it('unit: F to C', () => {
		expect(near(convertUnit(32, '°F', '°C', UnitKind.temperature), 0)).toBe(true)
	})
	it('unit: KB to B', () => {
		expect(convertUnit(1, 'KB', 'B', UnitKind.data)).toBe(1024)
	})
	it('unit: bad unit -> nil', () => {
		expect(convertUnit(1, 'xx', 'm', UnitKind.length)).toBeNull()
	})
	it('unit: every kind has units', () => {
		expect(unitKinds.every((kind) => unitsOf(kind).length > 0)).toBe(true)
	})
})

// Zing additions.
describe('known answers', () => {
	it('converts within each kind', () => {
		expect(near(convertUnit(1, 'mi', 'km', UnitKind.length), 1.609344)).toBe(true)
		expect(near(convertUnit(1, 'lb', 'g', UnitKind.mass), 453.59237)).toBe(true)
		expect(near(convertUnit(1, 'atm', 'kPa', UnitKind.pressure), 101.325)).toBe(true)
		expect(near(convertUnit(1, 'kWh', 'kJ', UnitKind.energy), 3600)).toBe(true)
		expect(near(convertUnit(1, 'turn', 'deg', UnitKind.angle), 360)).toBe(true)
		expect(convertUnit(8, 'bit', 'B', UnitKind.data)).toBe(1)
	})
	it('temperature refuses units of another kind', () => {
		expect(convertUnit(1, 'm', '°C', UnitKind.temperature)).toBeNull()
		expect(convertUnit(1, '°C', 'm', UnitKind.temperature)).toBeNull()
	})
	it('formats like %g', () => {
		expect(formatUnitValue(3.280839895013123)).toBe('3.28084')
		expect(formatUnitValue(212)).toBe('212')
		expect(formatUnitValue(0)).toBe('0')
		expect(formatUnitValue(1234567)).toBe('1.23457e+06')
		expect(formatUnitValue(0.00001234)).toBe('1.234e-05')
		expect(formatUnitValue(0.0001)).toBe('0.0001')
		expect(formatUnitValue(-2.5)).toBe('-2.5')
	})
})
