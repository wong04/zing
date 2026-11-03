import { useState, type ReactNode } from 'react'
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

/** One banner: the error if there is one, since a description of an expression that cannot run reads back nonsense. */
function banner(expression: string, description: string | null, schedule: Schedule | null): ReactNode {
	if (expression.trim() === '') return null
	if (description === null) return <InfoBanner kind="warning">A cron expression has exactly five fields separated by spaces.</InfoBanner>
	if (schedule?.problem) return <InfoBanner kind="warning">{schedule.problem}</InfoBanner>
	return <InfoBanner kind="info">{description}</InfoBanner>
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
			{banner(expression, description, schedule)}
			<div className="tool-rows">
				{schedule?.runs.map((run, index) => (
					<ResultRow key={run.getTime()} label={`Next run ${index + 1}`} value={formatFullDateTime(run, { now })} mono={false} />
				))}
			</div>
		</ToolPage>
	)
}
