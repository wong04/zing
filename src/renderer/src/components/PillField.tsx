import { useId, useRef, useState, type InputHTMLAttributes, type KeyboardEvent, type ReactNode } from 'react'

export interface PillFieldProps
	extends Omit<InputHTMLAttributes<HTMLInputElement>, 'className' | 'onChange' | 'value' | 'size'> {
	readonly value: string
	readonly onChange: (value: string) => void
	readonly icon?: ReactNode
	readonly onSubmit?: () => void
	/** Shown above the field and read out with it. Omit only when the surrounding row already labels it. */
	readonly label?: string
}

/** A text field: recessed, hairline border, and a soft accent focus ring (Krackn `PillField`). */
export function PillField({ value, onChange, icon, onSubmit, label, id, onKeyDown, ...rest }: PillFieldProps) {
	const generated = useId()
	const inputId = id ?? generated
	const submit = (e: KeyboardEvent<HTMLInputElement>): void => {
		onKeyDown?.(e)
		if (e.key === 'Enter' && onSubmit) onSubmit()
	}
	const field = (
		<div className="pill-field">
			{icon ? <span className="pill-field-icon">{icon}</span> : null}
			<input id={inputId} value={value} onChange={(e) => onChange(e.target.value)} onKeyDown={submit} {...rest} />
		</div>
	)
	if (!label) return field
	return (
		<div className="labeled-field">
			<label htmlFor={inputId}>{label}</label>
			{field}
		</div>
	)
}

export interface SelectAllFieldProps {
	readonly value: string
	readonly onChange: (value: string) => void
	readonly onSubmit: () => void
	/** Called when focus leaves without submitting, so the caller can snap back to what is really loaded. */
	readonly onCancel?: () => void
	readonly placeholder?: string
	/** Shown, centred, while the field is not being edited: the site's name in place of its full address. */
	readonly idleText?: string
	readonly 'aria-label': string
	readonly onFocusChange?: (focused: boolean) => void
}

/**
 * Selects everything when it gains focus, as a browser's address bar does, so
 * clicking in and typing replaces the URL instead of inserting into it
 * (Krackn `SelectAllField`). Escape gives up the edit.
 */
export function SelectAllField({
	value,
	onChange,
	onSubmit,
	onCancel,
	placeholder,
	idleText,
	onFocusChange,
	'aria-label': label,
}: SelectAllFieldProps) {
	const [focused, setFocused] = useState(false)
	const input = useRef<HTMLInputElement>(null)
	// The click that focuses the field must not then collapse the selection on mouse-up.
	const selecting = useRef(false)

	const idle = !focused && idleText !== undefined && idleText !== ''
	const onKeyDown = (e: KeyboardEvent<HTMLInputElement>): void => {
		if (e.key === 'Enter') {
			onSubmit()
			return
		}
		if (e.key !== 'Escape') return
		e.preventDefault()
		onCancel?.()
		input.current?.blur()
	}

	return (
		<input
			ref={input}
			className={idle ? 'select-all-field idle' : 'select-all-field'}
			aria-label={label}
			placeholder={placeholder}
			spellCheck={false}
			readOnly={idle}
			value={idle ? idleText : value}
			onChange={(e) => onChange(e.target.value)}
			onPointerDown={() => {
				selecting.current = document.activeElement !== input.current
			}}
			onMouseUp={(e) => {
				if (!selecting.current) return
				selecting.current = false
				e.preventDefault()
			}}
			onFocus={(e) => {
				setFocused(true)
				onFocusChange?.(true)
				e.currentTarget.select()
			}}
			onBlur={() => {
				setFocused(false)
				onFocusChange?.(false)
				onCancel?.()
			}}
			onKeyDown={onKeyDown}
		/>
	)
}
