import { useState } from 'react'
import { IconButton } from '../../components'
import { CheckIcon, CloseGlyph, CopyIcon } from '../../components/icons'
import './math.css'

/** Copies `text`; a refused write shows on the button (its tooltip and icon) rather than vanishing. */
export function CopyButton({ text, label = 'Copy' }: { readonly text: string; readonly label?: string }) {
	const [state, setState] = useState<'idle' | 'copied' | 'failed'>('idle')
	const copy = async (): Promise<void> => {
		try {
			await navigator.clipboard.writeText(text)
			setState('copied')
		} catch {
			setState('failed')
		}
	}
	const title = state === 'failed' ? "Couldn't copy" : state === 'copied' ? 'Copied' : label
	return (
		<IconButton aria-label={label} title={title} size={24} onClick={() => void copy()}>
			{state === 'copied' ? <CheckIcon size={14} /> : state === 'failed' ? <CloseGlyph size={14} /> : <CopyIcon size={14} />}
		</IconButton>
	)
}

export interface ResultRowProps {
	readonly label: string
	readonly value: string
	readonly testId?: string
}

/** One labelled, selectable, copyable result (Krackn `ResultRow`). */
export function ResultRow({ label, value, testId }: ResultRowProps) {
	return (
		<div className="math-result-row" data-testid={testId}>
			<span className="math-result-label">{label}</span>
			<span className="math-result-value math-mono">{value}</span>
			<CopyButton text={value} label={`Copy ${label}`} />
		</div>
	)
}
