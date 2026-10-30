import { useRef, type KeyboardEvent, type PointerEvent } from 'react'
import { nudge, ratioOf, valueAtPointer, type SliderRange } from './sliderMath'

const KNOB = 18
const PAGE_STEPS = 5

export interface PillSliderProps extends SliderRange {
	readonly value: number
	readonly onChange: (value: number) => void
	readonly 'aria-label': string
	/** Spoken in place of the bare number, e.g. "12 hours". */
	readonly valueText?: string
}

/** A slider drawn by hand: capsule track, accent fill, white knob (Krackn `PillSlider`). */
export function PillSlider({ value, onChange, min, max, step, valueText, 'aria-label': label }: PillSliderProps) {
	const track = useRef<HTMLDivElement>(null)
	const range: SliderRange = { min, max, step }
	const ratio = ratioOf(value, range)

	const fromPointer = (e: PointerEvent): void => {
		const box = track.current?.getBoundingClientRect()
		if (!box) return
		onChange(valueAtPointer(e.clientX - box.left, box.width, KNOB, range))
	}

	const onKeyDown = (e: KeyboardEvent): void => {
		const change = keyChange(e.key)
		if (change === undefined) return
		e.preventDefault()
		if (change === 'min') onChange(min)
		else if (change === 'max') onChange(max)
		else onChange(nudge(value, change, range))
	}

	return (
		<div
			ref={track}
			className="pill-slider"
			role="slider"
			tabIndex={0}
			aria-label={label}
			aria-valuemin={min}
			aria-valuemax={max}
			aria-valuenow={value}
			aria-valuetext={valueText}
			onKeyDown={onKeyDown}
			onPointerDown={(e) => {
				e.currentTarget.setPointerCapture(e.pointerId)
				fromPointer(e)
			}}
			onPointerMove={(e) => {
				if (e.currentTarget.hasPointerCapture(e.pointerId)) fromPointer(e)
			}}
		>
			<div className="pill-slider-rail" />
			<div className="pill-slider-fill" style={{ width: `calc(${KNOB / 2}px + (100% - ${KNOB}px) * ${ratio})` }} />
			<div className="pill-slider-knob" style={{ left: `calc((100% - ${KNOB}px) * ${ratio})` }} />
		</div>
	)
}

function keyChange(key: string): number | 'min' | 'max' | undefined {
	switch (key) {
		case 'ArrowRight':
		case 'ArrowUp':
			return 1
		case 'ArrowLeft':
		case 'ArrowDown':
			return -1
		case 'PageUp':
			return PAGE_STEPS
		case 'PageDown':
			return -PAGE_STEPS
		case 'Home':
			return 'min'
		case 'End':
			return 'max'
		default:
			return undefined
	}
}
