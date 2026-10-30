export interface ToggleProps {
	readonly checked: boolean
	readonly onChange: (checked: boolean) => void
	/** Names the switch for assistive tech; the visible label usually lives in the row beside it. */
	readonly 'aria-label': string
	readonly disabled?: boolean
}

/** The pill switch: accent track when on, recessed when off (Krackn `PillToggleStyle`). */
export function Toggle({ checked, onChange, disabled, 'aria-label': label }: ToggleProps) {
	return (
		<button
			type="button"
			role="switch"
			aria-checked={checked}
			aria-label={label}
			disabled={disabled}
			className={checked ? 'pill-toggle on' : 'pill-toggle'}
			onClick={() => onChange(!checked)}
		>
			<span className="pill-toggle-thumb" />
		</button>
	)
}
