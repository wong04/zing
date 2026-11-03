import { useState } from 'react'
import { InfoBanner, PillField } from '../../components'
import { convertBase } from '@shared/devtools/numberBase'
import { ResultRow } from './MathParts'
import './math.css'

/** Number bases (Krackn `NumberBaseTool`): one value in decimal, hex, binary and octal at once. */
export function NumberBaseTool() {
	const [input, setInput] = useState('255')
	const result = convertBase(input)

	return (
		<div className="math-tool">
			<p className="math-subtitle">Decimal, hex, binary and octal at once. Accepts 0x, 0b and 0o prefixes.</p>
			<PillField aria-label="Number" placeholder="255, 0xFF, 0b1111_1111…" spellCheck={false} value={input} onChange={setInput} />
			{result ? (
				<div className="math-results">
					<ResultRow label="Decimal" value={result.decimal} testId="base-Decimal" />
					<ResultRow label="Hexadecimal" value={result.hex} testId="base-Hexadecimal" />
					<ResultRow label="Binary" value={result.binary} testId="base-Binary" />
					<ResultRow label="Octal" value={result.octal} testId="base-Octal" />
					<ResultRow label="As bytes" value={result.bytes} testId="base-As bytes" />
				</div>
			) : null}
			{!result && input !== '' ? <InfoBanner kind="warning">That isn&apos;t a number in any recognised base.</InfoBanner> : null}
		</div>
	)
}
