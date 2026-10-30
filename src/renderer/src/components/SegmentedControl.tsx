import { useRef, type KeyboardEvent } from 'react'

export type SegmentValue = string | number | boolean

export interface SegmentOption<T extends SegmentValue> {
	readonly value: T
	readonly label: string
}

export interface SegmentedControlProps<T extends SegmentValue> {
	readonly options: readonly SegmentOption<T>[]
	readonly value: T
	readonly onChange: (value: T) => void
	readonly 'aria-label': string
	/** Total width in px; segments share it equally. */
	readonly width?: number
}

/**
 * A recessed track with the selected segment raised on a plate: selection is
 * shown by elevation, not by an accent flood (Krackn `SegmentedControl`).
 * A radio group underneath: one tab stop, arrow keys move the choice.
 */
export function SegmentedControl<T extends SegmentValue>({
	options,
	value,
	onChange,
	width,
	'aria-label': label,
}: SegmentedControlProps<T>) {
	const refs = useRef<(HTMLButtonElement | null)[]>([])
	const selected = options.findIndex((o) => o.value === value)

	const move = (e: KeyboardEvent, delta: number): void => {
		e.preventDefault()
		const next = (selected + delta + options.length) % options.length
		const option = options[next]
		if (!option) return
		onChange(option.value)
		refs.current[next]?.focus()
	}
	const onKeyDown = (e: KeyboardEvent): void => {
		if (e.key === 'ArrowRight' || e.key === 'ArrowDown') move(e, 1)
		else if (e.key === 'ArrowLeft' || e.key === 'ArrowUp') move(e, -1)
	}

	return (
		<div className="segmented" role="radiogroup" aria-label={label} style={width ? { width } : undefined}>
			{options.map((option, index) => {
				const isSelected = index === selected
				return (
					<button
						key={String(option.value)}
						ref={(el) => {
							refs.current[index] = el
						}}
						type="button"
						role="radio"
						aria-checked={isSelected}
						tabIndex={isSelected || (selected === -1 && index === 0) ? 0 : -1}
						className={isSelected ? 'segment selected' : 'segment'}
						title={option.label}
						onClick={() => onChange(option.value)}
						onKeyDown={onKeyDown}
					>
						{option.label}
					</button>
				)
			})}
		</div>
	)
}
