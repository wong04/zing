import { useMemo, useState } from 'react'
import { regexMatchLimit } from '@shared/devtools/regex'
import { InfoBanner, PillField, Toggle } from '../../components'
import { ResultRow, TextArea, ToolPage } from '../parts'
import { describeFailure } from './parts'
import type { RegexReply } from './regex.worker'
import { WorkerTimeoutError } from './runInWorker'
import { useTask, type TaskState } from './useTask'
import { runRegex } from './workers'

const sampleSubject = 'contact alice@example.com or bob@test.org'

export function RegexTool() {
	const [pattern, setPattern] = useState(String.raw`(\w+)@(\w+\.\w+)`)
	const [subject, setSubject] = useState(sampleSubject)
	const [ignoreCase, setIgnoreCase] = useState(false)
	const request = useMemo(() => (pattern === '' ? null : { pattern, subject, ignoreCase }), [pattern, subject, ignoreCase])
	const task = useTask(request, runRegex)

	return (
		<ToolPage>
			<PillField aria-label="Pattern" value={pattern} onChange={setPattern} placeholder="pattern" spellCheck={false} />
			<span className="dev-toggle">
				<Toggle checked={ignoreCase} onChange={setIgnoreCase} aria-label="Case insensitive" />
				Case insensitive
			</span>
			<TextArea label="Test string" value={subject} onChange={setSubject} rows={4} />
			<Outcome task={task} />
		</ToolPage>
	)
}

function Outcome({ task }: { readonly task: TaskState<RegexReply> }) {
	if (task.kind === 'idle') return null
	if (task.kind === 'running') return <InfoBanner kind="info">Matching…</InfoBanner>
	if (task.kind === 'failed') {
		if (task.error instanceof WorkerTimeoutError) {
			return <InfoBanner kind="warning">The tester gave up after {task.error.milliseconds / 1000} seconds. This pattern takes too long on this text, usually from nested repeats such as (a+)+.</InfoBanner>
		}
		return <InfoBanner kind="warning">{describeFailure(task.error)}</InfoBanner>
	}
	const reply = task.value
	if (!reply.ok) return <InfoBanner kind="warning">{reply.message}</InfoBanner>
	if (reply.matches.length === 0) return <InfoBanner kind="info">No matches.</InfoBanner>
	return (
		<>
			<span className="tool-label">{reply.matches.length === 1 ? '1 match' : `${reply.matches.length} matches`}</span>
			{reply.more && <InfoBanner kind="info">Showing the first {regexMatchLimit} matches. There are more.</InfoBanner>}
			<div className="tool-rows">
				{reply.matches.map((match, index) => (
					<div key={`${match.range}-${index}`} className="tool-rows">
						<ResultRow label={`Match ${match.range}`} value={match.text} />
						{match.groups.map((group, g) => (
							<ResultRow key={g} label={`  group ${g + 1}`} value={group} />
						))}
					</div>
				))}
			</div>
		</>
	)
}
