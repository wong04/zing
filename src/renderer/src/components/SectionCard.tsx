import type { ReactNode } from 'react'
import { InfoIcon, ShieldAlertIcon } from './icons'

export interface SectionCardProps {
	readonly title: string
	readonly children: ReactNode
	/** Tighter spacing, for tools shown in a floating panel (Krackn's `inFloatingPanel`). */
	readonly compact?: boolean
}

/**
 * A titled group, laid out the way System Settings groups controls: a small
 * secondary heading sitting above the card, the controls inside it. A title
 * inside the card at heading weight made every page a stack of competing
 * headlines (Krackn `SectionCard`).
 */
export function SectionCard({ title, children, compact = false }: SectionCardProps) {
	return (
		<section className={compact ? 'section-card compact' : 'section-card'} aria-label={title || undefined}>
			{title ? <h3 className="section-card-title">{title}</h3> : null}
			<div className="card-surface section-card-body">{children}</div>
		</section>
	)
}

export type BannerKind = 'info' | 'warning'

export interface InfoBannerProps {
	readonly kind: BannerKind
	readonly children: ReactNode
}

/** Krackn `InfoBanner`: a quiet note, or a caution tinted with the fixed status colour. */
export function InfoBanner({ kind, children }: InfoBannerProps) {
	return (
		<div className={`info-banner ${kind}`} role={kind === 'warning' ? 'alert' : 'note'}>
			{kind === 'warning' ? <ShieldAlertIcon size={16} /> : <InfoIcon size={16} />}
			<div>{children}</div>
		</div>
	)
}
