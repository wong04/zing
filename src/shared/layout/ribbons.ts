/** A favourite's ribbon, and the gap between ribbons, in px. */
export const RIBBON = { width: 40, gap: 4 } as const

/** How many ribbons a row shows: when they don't all fit, the "+N" button takes the last slot. */
export function ribbonLayout(count: number, innerWidth: number): { shown: number; more: number } {
	const slots = Math.max(1, Math.floor((innerWidth + RIBBON.gap) / (RIBBON.width + RIBBON.gap)))
	if (count <= slots) return { shown: count, more: 0 }
	const shown = slots - 1
	return { shown, more: count - shown }
}
