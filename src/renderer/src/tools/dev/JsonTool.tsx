import { useMemo, useState } from 'react'
import { formatJSON, workerThreshold, type JSONMode } from '@shared/devtools/jsonFormat'
import { InfoBanner, SegmentedControl } from '../../components'
import { DevTool, describeFailure, TextArea } from './parts'
import { useTask } from './useTask'
import { runJSON } from './workers'

const modes = [
	{ value: 'pretty', label: 'Pretty' },
	{ value: 'minify', label: 'Minify' },
] as const

export function JsonTool() {
	const [input, setInput] = useState('')
	const [mode, setMode] = useState<JSONMode>('pretty')
	const large = input.length > workerThreshold
	// Small documents format as you type; a big one goes to a worker so typing and the rest of the app stay live.
	const quick = useMemo(() => (large ? null : formatJSON(input, mode)), [large, input, mode])
	const request = useMemo(() => (large ? { text: input, mode } : null), [large, input, mode])
	const slow = useTask(request, runJSON)

	const outcome = quick ?? (slow.kind === 'done' ? slow.value : null)

	return (
		<DevTool>
			<SegmentedControl aria-label="Format" options={modes} value={mode} onChange={setMode} />
			<TextArea label="JSON input" value={input} onChange={setInput} rows={7} placeholder='{"a": 1}' />
			{slow.kind === 'running' ? <InfoBanner kind="info">Formatting a large document…</InfoBanner> : null}
			{slow.kind === 'failed' ? <InfoBanner kind="warning">{describeFailure(slow.error)}</InfoBanner> : null}
			{outcome?.ok === false ? (
				<InfoBanner kind="warning">
					Line {outcome.line}: {outcome.message}
				</InfoBanner>
			) : null}
			{outcome?.ok === true && input !== '' ? <InfoBanner kind="info">Valid JSON.</InfoBanner> : null}
			<TextArea label="JSON output" value={outcome?.ok ? outcome.text : ''} rows={9} readOnly />
		</DevTool>
	)
}
