import { useEffect, useId, useState, type ReactNode } from 'react'
import { CheckIcon, CloseGlyph, CopyIcon, IconButton } from '../components'
import './tools.css'

const resetAfterMs = 2000

type CopyOutcome = { readonly value: string; readonly kind: 'copied' | 'failed' }

export interface CopyButtonProps {
	/** What a click copies. Nothing to copy disables the button. */
	readonly value: string
	/** Names what is copied, for the tooltip and assistive tech: "Copy {label}". */
	readonly label: string
}

/** Copies `value`. The result shows on the button for two seconds, and only against the value it was for. */
export function CopyButton({ value, label }: CopyButtonProps) {
	const [outcome, setOutcome] = useState<CopyOutcome | null>(null)
	useEffect(() => {
		if (outcome === null) return
		const timer = setTimeout(() => setOutcome(null), resetAfterMs)
		return () => clearTimeout(timer)
	}, [outcome])
	// A new value means the tick is about the old one, so it is not shown.
	const state = outcome?.value === value ? outcome.kind : 'idle'
	const copy = async (): Promise<void> => {
		try {
			await navigator.clipboard.writeText(value)
			setOutcome({ value, kind: 'copied' })
		} catch (error) {
			console.error('Copy failed', error)
			setOutcome({ value, kind: 'failed' })
		}
	}
	const name = state === 'copied' ? 'Copied' : state === 'failed' ? 'Copy failed' : `Copy ${label}`
	return (
		<IconButton aria-label={name} title={state === 'failed' ? "Couldn't copy" : name} size={24} disabled={value === ''} onClick={() => void copy()}>
			{state === 'copied' ? <CheckIcon size={14} /> : state === 'failed' ? <CloseGlyph size={14} /> : <CopyIcon size={14} />}
		</IconButton>
	)
}

export interface ResultRowProps {
	readonly label: string
	readonly value: string
	/** On the value, so a test reads just the text. */
	readonly testId?: string
	/** Proportional type for prose-like values (dates); monospace for digests, codes, ids. */
	readonly mono?: boolean
}

/** A labelled read-only value with a copy button (Krackn `ResultRow`). */
export function ResultRow({ label, value, testId, mono = true }: ResultRowProps) {
	return (
		<div className="tool-result" role="group" aria-label={label}>
			<span className="tool-result-label">{label}</span>
			<span className={mono ? 'tool-result-value mono' : 'tool-result-value'} data-testid={testId}>
				{value === '' ? '—' : value}
			</span>
			<CopyButton value={value} label={label} />
		</div>
	)
}

export interface ToolPageProps {
	/** One line under which the tool starts: what it does. */
	readonly subtitle?: string
	readonly children: ReactNode
}

/** The frame every tool sits in: scrolls on its own, so a tall result never grows the panel. */
export function ToolPage({ subtitle, children }: ToolPageProps) {
	return (
		<div className="tool-page">
			{subtitle === undefined ? null : <p className="tool-subtitle">{subtitle}</p>}
			{children}
		</div>
	)
}

export interface TextAreaProps {
	/** Names the box for assistive tech and, shown above it, for everyone else. */
	readonly label: string
	readonly value: string
	readonly onChange?: (value: string) => void
	readonly rows?: number
	readonly placeholder?: string
	/** A result: selectable, with a copy button, but not editable. */
	readonly readOnly?: boolean
}

export function TextArea({ label, value, onChange, rows = 5, placeholder, readOnly = false }: TextAreaProps) {
	const id = useId()
	return (
		<div className="tool-field">
			<div className="tool-field-head">
				<label htmlFor={id} className="tool-label">
					{label}
				</label>
				{readOnly ? <CopyButton value={value} label={label} /> : null}
			</div>
			<textarea
				id={id}
				className="tool-text"
				value={value}
				rows={rows}
				placeholder={placeholder}
				readOnly={readOnly}
				spellCheck={false}
				autoCapitalize="off"
				autoComplete="off"
				onChange={(e) => onChange?.(e.target.value)}
			/>
		</div>
	)
}
