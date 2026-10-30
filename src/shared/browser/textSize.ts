/** Text-size steps in percent. Distinct from page zoom: this scales type only, via a CSS override. */
export const TEXT_SIZE_STEPS = [70, 80, 90, 100, 110, 125, 150, 175, 200] as const
export const DEFAULT_TEXT_SIZE = 100

function indexOf(percent: number): number {
	const at = TEXT_SIZE_STEPS.indexOf(percent as (typeof TEXT_SIZE_STEPS)[number])
	return at === -1 ? TEXT_SIZE_STEPS.indexOf(DEFAULT_TEXT_SIZE) : at
}

export function biggerText(percent: number): number {
	return TEXT_SIZE_STEPS[Math.min(indexOf(percent) + 1, TEXT_SIZE_STEPS.length - 1)]!
}

export function smallerText(percent: number): number {
	return TEXT_SIZE_STEPS[Math.max(indexOf(percent) - 1, 0)]!
}

/** `!important` because pages set their own root font size; the override has to win. */
export function textSizeCss(percent: number): string {
	return `html { font-size: ${percent}% !important; }`
}
