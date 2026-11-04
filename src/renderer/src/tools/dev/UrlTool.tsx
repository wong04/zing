import { useState } from 'react'
import { MalformedPercentEncodingError, urlDecode, urlEncode } from '@shared/devtools/encoding'
import { InfoBanner, SegmentedControl } from '../../components'
import { TextArea, ToolPage } from '../parts'

const directions = [
	{ value: 'encode', label: 'Encode' },
	{ value: 'decode', label: 'Decode' },
] as const

type Outcome = { readonly text: string; readonly problem: string | null }

function convert(input: string, direction: 'encode' | 'decode'): Outcome {
	if (direction === 'encode') return { text: urlEncode(input), problem: null }
	try {
		return { text: urlDecode(input), problem: null }
	} catch (error) {
		if (error instanceof MalformedPercentEncodingError) return { text: '', problem: error.message }
		throw error
	}
}

export function UrlTool() {
	const [input, setInput] = useState('')
	const [direction, setDirection] = useState<'encode' | 'decode'>('encode')
	const { text, problem } = convert(input, direction)

	return (
		<ToolPage>
			<SegmentedControl aria-label="Direction" options={directions} value={direction} onChange={setDirection} />
			<TextArea label="URL input" value={input} onChange={setInput} rows={6} />
			{problem === null ? null : <InfoBanner kind="warning">{problem}</InfoBanner>}
			<TextArea label="URL output" value={text} rows={6} readOnly />
		</ToolPage>
	)
}
