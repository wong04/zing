import { useState } from 'react'
import { InfoBanner, PillField, PillMenu } from '../../components'
import { convertUnit, formatUnitValue, UnitKind, unitKinds, unitsOf } from '@shared/devtools/units'
import { ResultRow } from './MathParts'
import './math.css'

export class UnitMismatchError extends Error {
	override name = 'UnitMismatchError'
	constructor(from: string, to: string, kind: UnitKind) {
		super(`${from} and ${to} are not both ${kind} units`)
	}
}

const kindOptions = unitKinds.map((kind) => ({ value: kind, label: kind }))
const unitOptions = (kind: UnitKind) => unitsOf(kind).map((unit) => ({ value: unit, label: unit }))

/** The first two units of a kind, which a new kind starts from. */
function defaultPair(kind: UnitKind): { from: string; to: string } {
	const [first = '', second = first] = unitsOf(kind)
	return { from: first, to: second }
}

function convert(amount: string, from: string, to: string, kind: UnitKind): string | null {
	if (amount.trim() === '') return null
	const value = Number(amount)
	if (!Number.isFinite(value)) return null
	const result = convertUnit(value, from, to, kind)
	if (result === null) throw new UnitMismatchError(from, to, kind)
	return `${formatUnitValue(result)} ${to}`
}

/** Unit converter (Krackn `UnitTool`). */
export function UnitsTool() {
	const [kind, setKind] = useState<UnitKind>(UnitKind.length)
	const [amount, setAmount] = useState('1')
	const [pair, setPair] = useState(() => ({ from: 'm', to: 'ft' }))

	const changeKind = (next: UnitKind): void => {
		setKind(next)
		setPair(defaultPair(next))
	}
	const result = convert(amount, pair.from, pair.to, kind)

	return (
		<div className="math-tool">
			<p className="math-subtitle">Length, mass, temperature, data, pressure, energy and angle.</p>
			<div className="math-row">
				<PillMenu options={kindOptions} value={kind} onChange={changeKind} aria-label="Unit kind" />
			</div>
			<div className="math-row">
				<PillField aria-label="Amount" placeholder="1" inputMode="decimal" value={amount} onChange={setAmount} />
				<PillMenu options={unitOptions(kind)} value={pair.from} onChange={(from) => setPair({ ...pair, from })} aria-label="From unit" />
				<span className="math-arrow" aria-hidden="true">→</span>
				<PillMenu options={unitOptions(kind)} value={pair.to} onChange={(to) => setPair({ ...pair, to })} aria-label="To unit" />
			</div>
			{result !== null ? <ResultRow label="Result" value={result} testId="unit-result" /> : null}
			{result === null && amount.trim() !== '' ? <InfoBanner kind="warning">Enter a number to convert.</InfoBanner> : null}
		</div>
	)
}
