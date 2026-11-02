/** A favourite's ribbon, and the gap between ribbons, in px. */
export const RIBBON = { width: 40, gap: 4 } as const

export interface Ribbon {
	readonly width: number
	readonly gap: number
}

/** How many ribbons a row shows: when they don't all fit, the "+N" button takes the last slot. */
export function ribbonLayout(count: number, innerWidth: number, ribbon: Ribbon = RIBBON): { shown: number; more: number } {
	const slots = Math.max(1, Math.floor((innerWidth + ribbon.gap) / (ribbon.width + ribbon.gap)))
	if (count <= slots) return { shown: count, more: 0 }
	const shown = slots - 1
	return { shown, more: count - shown }
}
