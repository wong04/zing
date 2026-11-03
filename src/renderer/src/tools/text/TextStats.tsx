import { useMemo, useState } from 'react'
import { analyze } from '@shared/devtools/textStats'
import { ResultRow, TextArea, ToolPage } from '../parts'

export function TextStats() {
	const [input, setInput] = useState('')
	const stats = useMemo(() => analyze(input), [input])
	const rows: readonly (readonly [string, string, string])[] = [
		['Characters', 'characters', String(stats.characters)],
		['No spaces', 'charactersNoSpaces', String(stats.charactersNoSpaces)],
		['Words', 'words', String(stats.words)],
		['Lines', 'lines', String(stats.lines)],
		['Sentences', 'sentences', String(stats.sentences)],
		['Paragraphs', 'paragraphs', String(stats.paragraphs)],
		['Approx. tokens', 'approxTokens', String(stats.approxTokens)],
		['Reading time', 'readingMinutes', `${stats.readingMinutes.toFixed(1)} min`],
	]
	return (
		<ToolPage subtitle="Counts for writing and for preparing training data.">
			<TextArea label="Text" value={input} onChange={setInput} />
			<div className="tool-rows">
				{rows.map(([label, key, value]) => (
					<ResultRow key={key} label={label} value={value} testId={`stat-${key}`} />
				))}
			</div>
		</ToolPage>
	)
}
