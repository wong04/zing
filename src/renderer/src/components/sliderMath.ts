// The arithmetic behind PillSlider (Krackn Components.swift `PillSlider`):
// a pointer position or a key press becomes a stepped value inside the range.

export interface SliderRange {
	readonly min: number
	readonly max: number
	readonly step: number
}

function clamp(value: number, min: number, max: number): number {
	return Math.min(Math.max(value, min), max)
}

/** Rounds to the step grid, then to a few decimals so 0.1 steps don't print as 0.30000000000000004. */
function snap(value: number, { min, max, step }: SliderRange): number {
	const stepped = Math.round((value - min) / step) * step + min
	return clamp(Number(stepped.toFixed(6)), min, max)
}

export function ratioOf(value: number, { min, max }: SliderRange): number {
	if (max === min) return 0
	return clamp((value - min) / (max - min), 0, 1)
}

export function valueAtRatio(ratio: number, range: SliderRange): number {
	return snap(range.min + clamp(ratio, 0, 1) * (range.max - range.min), range)
}

/**
 * The knob's centre travels from half a knob in to half a knob short of the
 * far end, so the pointer maps through that inset track, not the full width.
 */
export function valueAtPointer(x: number, trackWidth: number, knob: number, range: SliderRange): number {
	const travel = trackWidth - knob
	if (travel <= 0) return range.min
	return valueAtRatio((x - knob / 2) / travel, range)
}

export function nudge(value: number, steps: number, range: SliderRange): number {
	return snap(value + steps * range.step, range)
}
