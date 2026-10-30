/** Chromium's own page-zoom steps, so Ctrl+Plus feels the same as in Chrome and Edge. */
export const ZOOM_FACTORS = [0.25, 0.33, 0.5, 0.67, 0.75, 0.8, 0.9, 1, 1.1, 1.25, 1.5, 1.75, 2, 2.5, 3, 4, 5] as const

const EPSILON = 0.001

export function zoomInFactor(current: number): number {
	return ZOOM_FACTORS.find((f) => f > current + EPSILON) ?? current
}

export function zoomOutFactor(current: number): number {
	return [...ZOOM_FACTORS].reverse().find((f) => f < current - EPSILON) ?? current
}
