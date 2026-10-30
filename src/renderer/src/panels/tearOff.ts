// Port of Krackn's TearOffModifier (App/FloatingPanels.swift) as a state
// machine, so the click / hold / drag arbitration is testable without a DOM.
//
// A press that is held still becomes "armed": letting go somewhere else tears
// the item off into a panel. Moving before the hold completes means it was a
// drag of something else (a scroll, a file), so it is neither a tear-off nor a
// click. The gesture owns the tap too: a button underneath would fire as well
// and a completed tear-off would both open a panel and navigate.

export type Release = 'tear-off' | 'tap' | 'none'

/** How long the press is held before it arms. */
export const HOLD_MS = 380
/** Travel (Manhattan distance, px) that turns a press into a drag of something else. */
export const SLOP = 12

export class TearOffGesture {
	#pressed = false
	#armed = false
	#cancelled = false

	get armed(): boolean {
		return this.#armed
	}

	press(): void {
		this.#pressed = true
		this.#armed = false
		this.#cancelled = false
	}

	/** The hold timer fired. */
	hold(): void {
		if (this.#pressed && !this.#cancelled) this.#armed = true
	}

	move(dx: number, dy: number): void {
		if (!this.#pressed || this.#armed) return
		if (Math.abs(dx) + Math.abs(dy) > SLOP) this.#cancelled = true
	}

	/** Whether the hold timer should still be running. */
	get holding(): boolean {
		return this.#pressed && !this.#armed && !this.#cancelled
	}

	release(): Release {
		const result: Release = !this.#pressed ? 'none' : this.#armed ? 'tear-off' : this.#cancelled ? 'none' : 'tap'
		this.#pressed = false
		this.#armed = false
		this.#cancelled = false
		return result
	}
}
