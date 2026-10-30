import { useEffect, useId, useRef, useState, type KeyboardEvent } from 'react'
import { ChevronUpDownIcon } from './icons'
import type { SegmentOption, SegmentValue } from './SegmentedControl'

export interface PillMenuProps<T extends SegmentValue> {
	readonly options: readonly SegmentOption<T>[]
	readonly value: T
	readonly onChange: (value: T) => void
	readonly 'aria-label': string
}

interface Placement {
	readonly left: number
	readonly top: number
	readonly minWidth: number
}

const LIST_GAP = 4

/**
 * A dropdown drawn by hand (Krackn `PillMenu`): a recessed button that opens
 * a listbox. The list is fixed-positioned so a scrolling sheet can't clip it.
 */
export function PillMenu<T extends SegmentValue>({ options, value, onChange, 'aria-label': label }: PillMenuProps<T>) {
	const listId = useId()
	const button = useRef<HTMLButtonElement>(null)
	const list = useRef<HTMLUListElement>(null)
	const [placement, setPlacement] = useState<Placement | null>(null)
	const [active, setActive] = useState(0)
	const selected = options.findIndex((o) => o.value === value)
	const open = placement !== null

	useEffect(() => {
		if (!open) return
		list.current?.focus()
		const away = (e: PointerEvent): void => {
			if (e.target instanceof Node && (button.current?.contains(e.target) || list.current?.contains(e.target))) return
			setPlacement(null)
		}
		document.addEventListener('pointerdown', away)
		return () => document.removeEventListener('pointerdown', away)
	}, [open])

	const show = (): void => {
		const box = button.current?.getBoundingClientRect()
		if (!box) return
		setPlacement({ left: box.left, top: box.bottom + LIST_GAP, minWidth: box.width })
		setActive(Math.max(selected, 0))
	}
	const close = (): void => {
		setPlacement(null)
		button.current?.focus()
	}
	const choose = (index: number): void => {
		const option = options[index]
		if (option) onChange(option.value)
		close()
	}

	const onButtonKey = (e: KeyboardEvent): void => {
		if (e.key !== 'ArrowDown' && e.key !== 'ArrowUp') return
		e.preventDefault()
		show()
	}
	const onListKey = (e: KeyboardEvent): void => {
		switch (e.key) {
			case 'ArrowDown':
				setActive((i) => (i + 1) % options.length)
				break
			case 'ArrowUp':
				setActive((i) => (i - 1 + options.length) % options.length)
				break
			case 'Home':
				setActive(0)
				break
			case 'End':
				setActive(options.length - 1)
				break
			case 'Enter':
			case ' ':
				choose(active)
				break
			case 'Escape':
				// The sheet closes on Escape too; this one is only for the list.
				e.stopPropagation()
				close()
				break
			case 'Tab':
				setPlacement(null)
				return
			default:
				return
		}
		e.preventDefault()
	}

	return (
		<>
			<button
				ref={button}
				type="button"
				className="pill-menu"
				aria-label={label}
				aria-haspopup="listbox"
				aria-expanded={open}
				aria-controls={open ? listId : undefined}
				onClick={() => (open ? setPlacement(null) : show())}
				onKeyDown={onButtonKey}
			>
				<span className="pill-menu-label">{options[selected]?.label ?? ''}</span>
				<ChevronUpDownIcon size={12} />
			</button>
			{placement ? (
				<ul
					ref={list}
					id={listId}
					role="listbox"
					tabIndex={-1}
					aria-label={label}
					aria-activedescendant={`${listId}-${active}`}
					className="pill-menu-list"
					style={placement}
					onKeyDown={onListKey}
				>
					{options.map((option, index) => (
						<li
							key={String(option.value)}
							id={`${listId}-${index}`}
							role="option"
							aria-selected={index === selected}
							className={index === active ? 'pill-menu-option active' : 'pill-menu-option'}
							onPointerEnter={() => setActive(index)}
							onClick={() => choose(index)}
						>
							{option.label}
						</li>
					))}
				</ul>
			) : null}
		</>
	)
}
