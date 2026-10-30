import type { ButtonHTMLAttributes, ReactNode } from 'react'

export type PillButtonStyle = 'filled' | 'outlined' | 'ghost'
export type PillButtonSize = 'regular' | 'large' | 'small'

export interface PillButtonProps extends Omit<ButtonHTMLAttributes<HTMLButtonElement>, 'className'> {
	readonly icon?: ReactNode
	/**
	 * filled: the one primary action on a screen. outlined: secondary actions.
	 * ghost: tertiary, inline actions with no resting chrome.
	 */
	readonly variant?: PillButtonStyle
	readonly size?: PillButtonSize
}

/** The app's button (Krackn `PillButton`). */
export function PillButton({ icon, variant = 'filled', size = 'regular', type = 'button', children, ...rest }: PillButtonProps) {
	return (
		<button type={type} className={`pill-button ${variant} ${size}`} {...rest}>
			{icon}
			{children}
		</button>
	)
}

export interface IconButtonProps extends Omit<ButtonHTMLAttributes<HTMLButtonElement>, 'className' | 'type' | 'children'> {
	readonly children: ReactNode
	/** Required: a glyph alone says nothing to a screen reader. */
	readonly 'aria-label': string
	readonly size?: number
	/** An accent disc, for the rare icon that is the primary action. */
	readonly accent?: boolean
}

/** A glyph-only button: borderless at rest, a soft fill under the pointer (Krackn `IconButton`). */
export function IconButton({ children, size = 30, accent = false, style, title, ...rest }: IconButtonProps) {
	return (
		<button
			type="button"
			className={accent ? 'pill-icon-button accent' : 'pill-icon-button'}
			style={{ width: size, height: size, ...style }}
			title={title ?? rest['aria-label']}
			{...rest}
		>
			{children}
		</button>
	)
}
