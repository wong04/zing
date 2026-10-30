import type { Clock } from './clock'

/** Keeps a floor under the delay so a target in the past can't become a hot loop. */
const MIN_DELAY_MS = 50
/** setTimeout stores its delay in 32 bits; a longer wait just fires early and is recomputed. */
const MAX_DELAY_MS = 2 ** 31 - 1

/** One pending wake-up at an absolute time. Re-arming for the same time is a no-op. */
export class OneShotTimer {
	#handle: ReturnType<typeof setTimeout> | undefined
	#target: number | undefined

	constructor(
		private readonly clock: Clock,
		private readonly fire: () => void,
	) {}

	armAt(target: number | undefined): void {
		if (target === this.#target) return
		this.cancel()
		if (target === undefined) return
		this.#target = target
		const delay = Math.min(Math.max(target - this.clock.now(), MIN_DELAY_MS), MAX_DELAY_MS)
		this.#handle = setTimeout(() => {
			this.#handle = undefined
			this.#target = undefined
			this.fire()
		}, delay)
	}

	cancel(): void {
		clearTimeout(this.#handle)
		this.#handle = undefined
		this.#target = undefined
	}
}
