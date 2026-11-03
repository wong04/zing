import { useState } from 'react'
import { caseStyles, convertCase } from '@shared/devtools/textCase'
import { OutputRow, ToolPage } from './parts'
import './text.css'

export default function TextCase() {
	const [input, setInput] = useState('')
	return (
		<ToolPage subtitle="Convert between naming conventions.">
			<textarea className="text-tool-area" aria-label="Text" spellCheck={false} value={input} onChange={(e) => setInput(e.target.value)} />
			<div className="text-tool-rows">
				{caseStyles.map((style) => (
					<OutputRow key={style} label={style} value={input === '' ? '—' : convertCase(input, style)} testId={`case-${style}`} />
				))}
			</div>
		</ToolPage>
	)
}
