import { useEffect, useState, type ReactNode } from 'react'
import { CopyIcon, FadingScroll, PillButton } from '../../components'

/** The frame every tool in this group shares: a scrolling page with a one-line subtitle. */
export function ToolPage({ subtitle, children }: { readonly subtitle: string; readonly children: ReactNode }) {
	return (
		<FadingScroll fadeHeight={22}>
			<div className="text-tool">
				<p className="text-tool-subtitle">{subtitle}</p>
				{children}
			</div>
		</FadingScroll>
	)
}

const resetAfterMs = 2000

export function CopyButton({ text, label }: { readonly text: string; readonly label: string }) {
	const [state, setState] = useState<'idle' | 'copied' | 'failed'>('idle')
	useEffect(() => {
		if (state === 'idle') return
		const timer = setTimeout(() => setState('idle'), resetAfterMs)
		return () => clearTimeout(timer)
	}, [state])
	const copy = async (): Promise<void> => {
		try {
			await navigator.clipboard.writeText(text)
			setState('copied')
		} catch (error) {
			console.error('Copy failed', error)
			setState('failed')
		}
	}
	const shown = state === 'copied' ? 'Copied' : state === 'failed' ? 'Copy failed' : label
	return (
		<PillButton variant="ghost" size="small" icon={<CopyIcon size={13} />} aria-label={state === 'idle' ? `Copy ${label}` : shown} onClick={() => void copy()}>
			{state === 'idle' ? null : shown}
		</PillButton>
	)
}

export interface OutputRowProps {
	readonly label: string
	readonly value: string
	readonly testId: string
}

export function OutputRow({ label, value, testId }: OutputRowProps) {
	return (
		<div className="output-row">
			<span className="output-row-label">{label}</span>
			<span className="output-row-value" data-testid={testId}>
				{value}
			</span>
			<CopyButton text={value} label={label} />
		</div>
	)
}
