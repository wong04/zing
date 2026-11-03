import { useState } from 'react'
import { caseStyles, convertCase } from '@shared/devtools/textCase'
import { ResultRow, TextArea, ToolPage } from '../parts'

export function TextCase() {
	const [input, setInput] = useState('')
	return (
		<ToolPage subtitle="Convert between naming conventions.">
			<TextArea label="Text" value={input} onChange={setInput} />
			<div className="tool-rows">
				{caseStyles.map((style) => (
					<ResultRow key={style} label={style} value={convertCase(input, style)} testId={`case-${style}`} />
				))}
			</div>
		</ToolPage>
	)
}
