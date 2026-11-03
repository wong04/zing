import { useMemo, useState } from 'react'
import { analyze } from '@shared/devtools/textStats'
import { OutputRow, ToolPage } from './parts'
import './text.css'

export default function TextStats() {
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
			<textarea className="text-tool-area" aria-label="Text" spellCheck={false} value={input} onChange={(e) => setInput(e.target.value)} />
			<div className="text-tool-rows">
				{rows.map(([label, key, value]) => (
					<OutputRow key={key} label={label} value={value} testId={`stat-${key}`} />
				))}
			</div>
		</ToolPage>
	)
}
