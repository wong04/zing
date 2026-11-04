import { useState } from 'react'
import { base64Decode, base64Encode } from '@shared/devtools/encoding'
import { InfoBanner, SegmentedControl } from '../../components'
import { TextArea, ToolPage } from '../parts'

const directions = [
	{ value: 'encode', label: 'Encode' },
	{ value: 'decode', label: 'Decode' },
] as const

export function Base64Tool() {
	const [input, setInput] = useState('')
	const [direction, setDirection] = useState<'encode' | 'decode'>('encode')
	const decoded = direction === 'decode' && input !== '' ? base64Decode(input) : ''
	const output = direction === 'encode' ? base64Encode(input) : decoded

	return (
		<ToolPage>
			<SegmentedControl aria-label="Direction" options={directions} value={direction} onChange={setDirection} />
			<TextArea label="Base64 input" value={input} onChange={setInput} rows={6} />
			{decoded === null ? <InfoBanner kind="warning">Not valid base64, or not UTF-8 text.</InfoBanner> : null}
			<TextArea label="Base64 output" value={output ?? ''} rows={6} readOnly />
		</ToolPage>
	)
}
