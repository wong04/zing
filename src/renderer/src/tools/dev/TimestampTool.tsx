import { useState } from 'react'
import { describeTimestamp } from '@shared/devtools/timestamp'
import { InfoBanner, PillButton, PillField } from '../../components'
import { ResultRow, ToolPage } from '../parts'

type Reading = { readonly input: string; readonly now: Date }

const unixNow = (now: Date): string => String(Math.trunc(now.getTime() / 1000))

function readingAt(input: string): Reading {
	return { input, now: new Date() }
}

export function TimestampTool() {
	// `now` is taken when the text changes, not on every render, so the "Relative" row doesn't drift under the reader.
	const [{ input, now }, setReading] = useState<Reading>(() => {
		const now = new Date()
		return { input: unixNow(now), now }
	})
	const rows = describeTimestamp(input, { now })

	return (
		<ToolPage>
			<div className="dev-row">
				<PillField aria-label="Timestamp" value={input} onChange={(value) => setReading(readingAt(value))} placeholder="1753800000" />
				<PillButton variant="outlined" size="small" onClick={() => setReading(readingAt(unixNow(new Date())))}>
					Now
				</PillButton>
			</div>
			{rows.length === 0 && input !== '' ? <InfoBanner kind="warning">That isn't a number.</InfoBanner> : null}
			<div className="tool-rows">
				{rows.map(([label, value]) => (
					<ResultRow key={label} label={label} value={value} mono={label !== 'Local time' && label !== 'Relative'} />
				))}
			</div>
		</ToolPage>
	)
}
