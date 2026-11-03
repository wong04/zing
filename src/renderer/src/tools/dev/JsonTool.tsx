import { useMemo, useState } from 'react'
import { formatJSON, tooLargeToShow, workerThreshold, type JSONMode } from '@shared/devtools/jsonFormat'
import { InfoBanner, SegmentedControl } from '../../components'
import { CopyButton, TextArea, ToolPage } from '../parts'
import { describeFailure } from './parts'
import { useTask } from './useTask'
import { WorkerTimeoutError } from './runInWorker'
import { jsonTimeoutMs, runJSON } from './workers'

const modes = [
	{ value: 'pretty', label: 'Pretty' },
	{ value: 'minify', label: 'Minify' },
] as const

function failureMessage(error: unknown): string {
	if (error instanceof WorkerTimeoutError) return `Formatting gave up after ${jsonTimeoutMs / 1000} seconds. This document is too large or too complex to format here.`
	return describeFailure(error)
}

export function JsonTool() {
	const [input, setInput] = useState('')
	const [mode, setMode] = useState<JSONMode>('pretty')
	const large = input.length > workerThreshold
	// Small documents format as you type; a big one goes to a worker so typing and the rest of the app stay live.
	const quick = useMemo(() => (large ? null : formatJSON(input, mode)), [large, input, mode])
	const request = useMemo(() => (large ? { text: input, mode } : null), [large, input, mode])
	const slow = useTask(request, runJSON)

	const outcome = quick ?? (slow.kind === 'done' ? slow.value : null)
	const shownText = outcome?.ok ? outcome.text : ''
	const tooLarge = useMemo(() => tooLargeToShow(shownText), [shownText])

	return (
		<ToolPage>
			<SegmentedControl aria-label="Format" options={modes} value={mode} onChange={setMode} />
			<TextArea label="JSON input" value={input} onChange={setInput} rows={7} placeholder='{"a": 1}' />
			{slow.kind === 'running' ? <InfoBanner kind="info">Formatting a large document…</InfoBanner> : null}
			{slow.kind === 'failed' ? <InfoBanner kind="warning">{failureMessage(slow.error)}</InfoBanner> : null}
			{outcome?.ok === false ? (
				<InfoBanner kind="warning">
					{outcome.line === null ? outcome.message : `Line ${outcome.line}: ${outcome.message}`}
				</InfoBanner>
			) : null}
			{outcome?.ok === true && input !== '' ? <InfoBanner kind="info">Valid JSON.</InfoBanner> : null}
			{tooLarge ? (
				<div className="dev-row">
					<InfoBanner kind="info">
						The result is {(shownText.length / 1_000_000).toFixed(1)} MB, too large to show here. Copy it instead.
					</InfoBanner>
					<CopyButton value={shownText} label="JSON output" />
				</div>
			) : (
				<TextArea label="JSON output" value={shownText} rows={9} readOnly />
			)}
		</ToolPage>
	)
}
