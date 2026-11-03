import { useId, useState, type ReactNode } from 'react'
import { CheckIcon, CopyIcon, IconButton } from '../../components'
import './dev.css'

/** The frame every developer tool sits in: scrolls on its own, so a tall result never grows the panel. */
export function DevTool({ children }: { readonly children: ReactNode }) {
	return <div className="dev-tool">{children}</div>
}

export interface CopyButtonProps {
	readonly text: string
	readonly label: string
}

/** Copies `text`; shows a tick for a moment so the click visibly did something. */
export function CopyButton({ text, label }: CopyButtonProps) {
	const [copied, setCopied] = useState(false)
	const copy = async (): Promise<void> => {
		await navigator.clipboard.writeText(text)
		setCopied(true)
		setTimeout(() => setCopied(false), 1200)
	}
	return (
		<IconButton aria-label={`Copy ${label}`} size={26} disabled={text === ''} onClick={() => void copy()}>
			{copied ? <CheckIcon size={14} /> : <CopyIcon size={14} />}
		</IconButton>
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
		<div className="dev-field">
			<div className="dev-field-head">
				<label htmlFor={id} className="dev-label">
					{label}
				</label>
				{readOnly ? <CopyButton text={value} label={label} /> : null}
			</div>
			<textarea
				id={id}
				className="dev-text"
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

export interface ResultRowProps {
	readonly label: string
	readonly value: string
	/** Proportional type for prose-like values (dates); monospace for digests, codes, ids. */
	readonly mono?: boolean
}

/** A labelled read-only value with a copy button (Krackn `ResultRow`). */
export function ResultRow({ label, value, mono = true }: ResultRowProps) {
	return (
		<div className="dev-result" role="group" aria-label={label}>
			<span className="dev-result-label">{label}</span>
			<span className={mono ? 'dev-result-value mono' : 'dev-result-value'}>{value === '' ? '—' : value}</span>
			<CopyButton text={value} label={label} />
		</div>
	)
}

/** A read-only block of code under a heading, e.g. a decoded JWT header. */
export function CodeBlock({ label, text }: { readonly label: string; readonly text: string }) {
	return (
		<div className="dev-code" role="group" aria-label={label}>
			<div className="dev-field-head">
				<span className="dev-label">{label}</span>
				<CopyButton text={text} label={label} />
			</div>
			<pre className="dev-pre">{text}</pre>
		</div>
	)
}

/** What to tell the person when a task failed with something unexpected. */
export function describeFailure(error: unknown): string {
	return error instanceof Error ? error.message : String(error)
}
