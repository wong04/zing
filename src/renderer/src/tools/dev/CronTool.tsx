import { useState } from 'react'
import { describeCron } from '@shared/devtools/cron'
import { CronProblem, nextCronRuns } from '@shared/devtools/cronNext'
import { formatFullDateTime } from '@shared/devtools/timestamp'
import { InfoBanner, PillField } from '../../components'
import { ResultRow, ToolPage } from '../parts'

const runCount = 5

type Reading = { readonly expression: string; readonly now: Date }

type Schedule = { readonly runs: readonly Date[]; readonly problem: string | null }

function scheduleOf(expression: string, now: Date): Schedule {
	try {
		return { runs: nextCronRuns(expression, now, runCount), problem: null }
	} catch (error) {
		if (error instanceof CronProblem) return { runs: [], problem: error.message }
		throw error
	}
}

export function CronTool() {
	// `now` is taken when the expression changes, so the listed runs don't shift while you read them.
	const [{ expression, now }, setReading] = useState<Reading>(() => ({ expression: '0 9 * * 1-5', now: new Date() }))
	const description = describeCron(expression)
	const schedule = description === null ? null : scheduleOf(expression, now)

	return (
		<ToolPage>
			<PillField
				aria-label="Cron expression"
				value={expression}
				onChange={(value) => setReading({ expression: value, now: new Date() })}
				placeholder="*/15 * * * *"
				spellCheck={false}
			/>
			<span className="tool-label">minute, hour, day of month, month, day of week</span>
			{expression.trim() === '' ? null : description === null ? (
				<InfoBanner kind="warning">A cron expression has exactly five fields separated by spaces.</InfoBanner>
			) : (
				<InfoBanner kind="info">{description}</InfoBanner>
			)}
			{schedule?.problem ? <InfoBanner kind="warning">{schedule.problem}</InfoBanner> : null}
			<div className="tool-rows">
				{schedule?.runs.map((run, index) => (
					<ResultRow key={run.getTime()} label={`Next run ${index + 1}`} value={formatFullDateTime(run, { now })} mono={false} />
				))}
			</div>
		</ToolPage>
	)
}
