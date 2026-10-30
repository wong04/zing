/** One display frame at 60 Hz. Main has no requestAnimationFrame, so a timer stands in. */
export const FRAME_MS = 16

/**
 * Coalesces bursts of changes into at most one flush per frame. A page load
 * fires title, URL, favicon and loading events within milliseconds; sending
 * each to the chrome renderer separately would re-render the strip each time.
 */
export class FrameBatcher {
	#timer: ReturnType<typeof setTimeout> | undefined

	constructor(
		private readonly flush: () => void,
		private readonly frameMs = FRAME_MS,
	) {}

	schedule(): void {
		if (this.#timer !== undefined) return
		this.#timer = setTimeout(() => {
			this.#timer = undefined
			this.flush()
		}, this.frameMs)
	}

	cancel(): void {
		clearTimeout(this.#timer)
		this.#timer = undefined
	}
}
